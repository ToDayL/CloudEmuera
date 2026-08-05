using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeCompatibility.Tests;

internal sealed record RuntimeScenarioReport(
    string FixtureId,
    string Profile,
    string Status,
    int AssertionCount,
    long ElapsedMilliseconds,
    string UpstreamCommit,
    string IntegrationVersion,
    IReadOnlyList<RuntimeScenarioAssertionEvidence> AssertionEvidence,
    IReadOnlyList<string> Errors);

internal sealed record RuntimeScenarioAssertionEvidence(
    string Name,
    bool Passed,
    [property: JsonPropertyName("verifiedByVisibleOutput")] bool VerifiedByVisibleOutput);

internal static class RuntimeScenarioRunner
{
    public static IReadOnlyList<string> FixtureIds { get; } = ["v18-core", "em-ee-core"];

    public static async Task<RuntimeScenarioReport> RunAsync(string repositoryRoot, string fixtureId)
    {
        string fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "runtime", fixtureId);
        if (!FixtureIds.Contains(fixtureId, StringComparer.Ordinal))
        {
            return Failed(fixtureId, "unknown", "Fixture is not listed in the runtime manifest.");
        }

        string profile = fixtureId == "v18-core"
            ? EmueraCompatibilityProfiles.V18Compatible
            : EmueraCompatibilityProfiles.EmEeCurrent;
        RuntimeSaveLayout saveLayout = fixtureId == "v18-core"
            ? RuntimeSaveLayout.Root
            : RuntimeSaveLayout.SavDirectory;
        string beforeDigest = DigestDirectory(fixtureRoot);
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();
        var assertionEvidence = new List<RuntimeScenarioAssertionEvidence>();
        int assertions = 0;
        string scratch = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-compat", Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(scratch, "interpreter");
        string workspaceRoot = Path.Combine(scratch, "workspace");
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var paths = new RuntimePaths(sessionRoot, fixtureRoot, workspaceRoot, saveLayout);
            Directory.CreateDirectory(paths.ConfigurationRoot);
            Directory.CreateDirectory(paths.TemporaryRoot);
            Directory.CreateDirectory(paths.RootSaveRoot);
            Directory.CreateDirectory(paths.SavDirectoryRoot);
            File.Copy(Path.Combine(fixtureRoot, "emuera.config"), Path.Combine(paths.ConfigurationRoot, "emuera.config"));

            var fileSystem = new LocalRuntimeFileSystem(paths);
            var clock = new TimeProviderRuntimeClock();
            var console = new StructuredGameConsole(clock);
            var imagePort = new RuntimeImageMetadataPort(fileSystem);
            var audioPort = new RecordingRuntimeAudioPort();
            var options = new EmueraRuntimeOptions(
                paths,
                console,
                fileSystem,
                clock,
                imagePort,
                audioPort,
                profile,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10));

            await using EmueraRuntimeHost host = EmueraRuntimeHost.Create(options);
            EmueraRuntimeResult initialized = await host.InitializeAsync().ConfigureAwait(false);
            Check(initialized.Status == EmueraRuntimeStatus.Completed, "Initialization did not complete.", errors, ref assertions);
            if (initialized.Status != EmueraRuntimeStatus.Completed)
            {
                errors.AddRange(initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
                return Report(initialized.Status.ToString());
            }

            Task<EmueraRuntimeResult> run = host.RunAsync();
            bool promptOpened = SpinWait.SpinUntil(() => console.CurrentPrompt is not null || run.IsCompleted, TimeSpan.FromSeconds(5));
            Check(promptOpened, "Runtime did not reach INPUT.", errors, ref assertions);
            ConsolePrompt? prompt = console.CurrentPrompt;
            Check(prompt is not null, "Runtime completed before opening a prompt.", errors, ref assertions);
            if (prompt is not null)
            {
                Check(prompt.InputType == ConsoleInputType.Integer, "Prompt type was not integer.", errors, ref assertions);
                string expectedPrefix = fixtureId == "v18-core"
                    ? "V18-START\nV18-READY\nV18-INPUT"
                    : "EMEE-START\nEMEE-INPUT";
                Check(
                    string.Equals(RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes), expectedPrefix, StringComparison.Ordinal),
                    "Visible output before INPUT did not match the fixture prefix.",
                    errors,
                    ref assertions);
                string input = ReadScenarioInput(Path.Combine(fixtureRoot, "scenario.json"));
                ConsoleInputResult submitted = console.SubmitInput(new ConsoleInputCommand(prompt.PromptId, $"compat-{fixtureId}", input));
                Check(submitted.Kind == ConsoleInputResultKind.Accepted, "Scenario input was not accepted.", errors, ref assertions);
            }

            EmueraRuntimeResult result = await run.ConfigureAwait(false);
            Check(result.Status == EmueraRuntimeStatus.Completed, "Runtime did not complete at QUIT.", errors, ref assertions);
            Check(result.Diagnostics.All(diagnostic => !diagnostic.IsFatal), "Runtime emitted a fatal diagnostic.", errors, ref assertions);
            Check(console.CurrentPrompt is null, "A prompt remained open after QUIT.", errors, ref assertions);

            ConsoleSnapshot snapshot = console.Snapshot;
            string actual = RuntimeTranscriptProjector.Project(snapshot.VisibleNodes);
            string expected = NormalizeExpected(File.ReadAllText(Path.Combine(fixtureRoot, "expected-transcript.txt")));
            Check(string.Equals(actual, expected, StringComparison.Ordinal), TranscriptDifference(expected, actual), errors, ref assertions);
            string expectedScoreLine = fixtureId == "v18-core" ? "V18-SCORE=3" : "EMEE-SCORE=3";
            bool scorePassed = actual.Split('\n').Contains(expectedScoreLine, StringComparer.Ordinal);
            Check(scorePassed, "Visible ERB output did not verify score=3.", errors, ref assertions);
            assertionEvidence.Add(new RuntimeScenarioAssertionEvidence("score=3", scorePassed, VerifiedByVisibleOutput: true));
            ConsoleFontStyle expectedStyle = fixtureId == "v18-core" ? ConsoleFontStyle.Bold : ConsoleFontStyle.Italic;
            Check(snapshot.VisibleNodes.OfType<TextNode>().Any(node => node.Style.Decorations.HasFlag(expectedStyle)), "Expected HTML style node was not emitted.", errors, ref assertions);
            string sprite = fixtureId == "v18-core" ? "V18_SPRITE" : "EMEE_SPRITE";
            Check(snapshot.VisibleNodes.OfType<ImageNode>().Any(node => node.AssetId.Value == sprite), "Expected sprite ImageNode was not emitted.", errors, ref assertions);
            Check(audioPort.PlayedRequests.Count == 0, "Fixture unexpectedly requested audio playback.", errors, ref assertions);
            Check(!fileSystem.FileExists(new RuntimeFilePath(RuntimeFileArea.Save, "save00.sav")), "P0-04 created save00.sav.", errors, ref assertions);
            Check(!fileSystem.FileExists(new RuntimeFilePath(RuntimeFileArea.Save, "global.sav")), "P0-04 created global.sav.", errors, ref assertions);
            IReadOnlyList<SequencedConsoleEvent> history = console.StateStore.History;
            Check(history.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, history.Count).Select(value => (long)value)), "Console event sequence was not continuous.", errors, ref assertions);
            Check(string.Equals(beforeDigest, DigestDirectory(fixtureRoot), StringComparison.Ordinal), "Fixture GameContent changed during execution.", errors, ref assertions);
            return Report(result.Status.ToString());
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            return Report("Failed");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        RuntimeScenarioReport Report(string status) => new(
            fixtureId,
            profile,
            errors.Count == 0 ? status : "Failed",
            assertions,
            stopwatch.ElapsedMilliseconds,
            RuntimeBaseline.UpstreamCommit,
            RuntimeBaseline.CloudEmueraIntegrationVersion,
            assertionEvidence,
            errors);
    }

    private static RuntimeScenarioReport Failed(string fixtureId, string profile, string error) => new(
        fixtureId,
        profile,
        "Failed",
        0,
        0,
        RuntimeBaseline.UpstreamCommit,
        RuntimeBaseline.CloudEmueraIntegrationVersion,
        [],
        [error]);

    private static string ReadScenarioInput(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement steps = document.RootElement.GetProperty("steps");
        foreach (JsonElement step in steps.EnumerateArray())
        {
            if (step.GetProperty("type").GetString() == "submitInput")
            {
                if (step.GetProperty("inputKind").GetString() != "integer")
                {
                    throw new InvalidDataException("The input-roundtrip scenario requires integer input.");
                }

                return step.GetProperty("value").GetString() ?? throw new InvalidDataException("Scenario input is missing.");
            }
        }

        throw new InvalidDataException("The scenario has no submitInput step.");
    }

    private static string NormalizeExpected(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    private static string TranscriptDifference(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int limit = Math.Min(expectedLines.Length, actualLines.Length);
        int index = 0;
        while (index < limit && expectedLines[index] == actualLines[index])
        {
            index++;
        }

        string expectedLine = index < expectedLines.Length ? expectedLines[index] : "<missing>";
        string actualLine = index < actualLines.Length ? actualLines[index] : "<missing>";
        string boundedActual = actual.Length <= 500 ? actual : actual[..500];
        return $"Transcript differs at line {index + 1}: expected '{expectedLine}', actual '{actualLine}'. Actual transcript: '{boundedActual.Replace("\n", "\\n", StringComparison.Ordinal)}'.";
    }

    private static string DigestDirectory(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Check(bool condition, string error, List<string> errors, ref int assertions)
    {
        assertions++;
        if (!condition)
        {
            errors.Add(error);
        }
    }
}
