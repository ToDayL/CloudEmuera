using System.Text.Json;
using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;

return await ValidatorProcess.RunAsync(args).ConfigureAwait(false);

internal static class ValidatorProcess
{
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        bool prepareOnly = args.Length == 3 && args[0] == "--root" && args[2] == "--prepare-config";
        if ((args.Length != 2 && !prepareOnly) || args[0] != "--root" || !Path.IsPathFullyQualified(args[1])) return 10;
        string root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root)) return 10;
        string temporary = Path.Combine(Path.GetTempPath(), "cloudemuera-validator", Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeConfigMaterializationResult config = RuntimeConfigMaterializer.Materialize(root);
            if (prepareOnly)
            {
                Write(new
                {
                    schemaVersion = 1,
                    canActivate = true,
                    diagnostics = config.Changed
                        ? new[]
                        {
                            new
                            {
                                code = config.Created ? "RUNTIME_CONFIG_GENERATED" : "RUNTIME_CONFIG_UPDATED",
                                severity = "WARNING",
                                path = "emuera.config",
                                message = config.Created
                                    ? "The pinned upstream runtime generated emuera.config from the game defaults."
                                    : "The pinned upstream runtime updated emuera.config using its normal versioning rules.",
                                activationBlocking = false,
                            },
                        }
                        : Array.Empty<object>(),
                });
                return 0;
            }

            Directory.CreateDirectory(temporary);
            // Validation only needs a private runtime root. Use the root-only
            // materializer so the validator does not build the legacy
            // per-file manifest before starting the parser.
            SessionRootLayout layout = new SessionRootLayoutBuilder(root, Path.Combine(temporary, "session"), temporary).BuildRootOnly();
            var fileSystem = new LocalRuntimeFileSystem(layout.RuntimePaths);
            var console = new StructuredGameConsole();
            double initTimeoutSeconds = double.TryParse(
                Environment.GetEnvironmentVariable("CLOUDEMUERA_VALIDATOR_INIT_TIMEOUT_SECONDS"),
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out double configured) && configured > 0 ? configured : 60;
            var options = new EmueraRuntimeOptions(
                layout.RuntimePaths,
                console,
                fileSystem,
                console.Clock,
                new RuntimeImageMetadataPort(fileSystem),
                new NoOpRuntimeAudioPort(),
                EmueraCompatibilityProfiles.V18Compatible,
                TimeSpan.FromSeconds(initTimeoutSeconds),
                TimeSpan.FromSeconds(1));
            await using EmueraRuntimeHost host = EmueraRuntimeHost.Create(options);
            EmueraRuntimeResult result = await host.InitializeAsync().ConfigureAwait(false);
            var diagnostics = result.Diagnostics.Take(128).Select(item => new
            {
                code = NormalizeCode(item.Code),
                severity = item.IsFatal ? "ERROR" : "WARNING",
                path = item.SourcePath,
                message = SafeMessage(item.Message),
                activationBlocking = item.IsFatal,
            }).ToArray();
            bool accepted = result.Status == EmueraRuntimeStatus.Completed && !diagnostics.Any(item => item.activationBlocking);
            Write(new { schemaVersion = 1, canActivate = accepted, diagnostics });
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or RuntimePathException)
        {
            Write(new
            {
                schemaVersion = 1,
                canActivate = false,
                diagnostics = new[] { new { code = "PARSER_INITIALIZATION_FAILED", severity = "ERROR", path = (string?)null, message = SafeMessage(exception.Message), activationBlocking = true } },
            });
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void Write<T>(T value) => Console.Out.Write(JsonSerializer.Serialize(value, ProtocolJson));
    private static string NormalizeCode(string code) => string.IsNullOrWhiteSpace(code) ? "PARSER_DIAGNOSTIC" : code.ToUpperInvariant().Replace('-', '_');
    private static string SafeMessage(string message)
    {
        string singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
    }
}
