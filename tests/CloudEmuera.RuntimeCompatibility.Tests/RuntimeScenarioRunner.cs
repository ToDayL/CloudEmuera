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
    IReadOnlyList<string> Errors,
    string Scenario,
    string Layout,
    string RunPhase);

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
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();
        var assertionEvidence = new List<RuntimeScenarioAssertionEvidence>();
        int assertions = 0;
        string reportLayout = "unknown";
        string scratch = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-compat", Guid.NewGuid().ToString("N"));
        string publishedGameRoot = Path.Combine(scratch, "published-game");
        string sessionRoot = Path.Combine(scratch, "session-root");
        string workspaceRoot = Path.Combine(scratch, "session-workspace");

        try
        {
            Directory.CreateDirectory(workspaceRoot);
            CopyPublishedGameContent(fixtureRoot, publishedGameRoot);
            SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
                publishedGameRoot,
                fixtureId);
            string beforeDigest = DigestDirectory(publishedGameRoot);
            SessionRootLayout layout = new SessionRootLayoutBuilder(
                publishedGameRoot,
                sessionRoot,
                workspaceRoot)
                .WithPublishedManifest(manifest)
                .Build();
            RuntimePaths paths = layout.RuntimePaths;
            reportLayout = paths.SaveLayout.ToString();

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
                ConsoleInputResult submitted = console.SubmitCurrentInput(new ConsoleInputAttempt($"compat-{fixtureId}", input));
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
            Check(
                snapshot.VisibleNodes.Any(node =>
                    node is SpriteNode spriteNode &&
                    ConsoleAssetIdCodec.TryDecodePath(spriteNode.AssetId.Value, out _) &&
                    spriteNode.SourceRect == new ConsoleRect(0, 0, 2, 2)),
                "Expected structured sprite node was not emitted.",
                errors,
                ref assertions);
            Check(audioPort.PlayedRequests.Count == 0, "Fixture unexpectedly requested audio playback.", errors, ref assertions);
            Check(!fileSystem.FileExists(new RuntimeFilePath(RuntimeFileArea.Save, "save00.sav")), "P0-04 created save00.sav.", errors, ref assertions);
            Check(!fileSystem.FileExists(new RuntimeFilePath(RuntimeFileArea.Save, "global.sav")), "P0-04 created global.sav.", errors, ref assertions);
            IReadOnlyList<SequencedConsoleEvent> history = console.StateStore.History;
            Check(history.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, history.Count).Select(value => (long)value)), "Console event sequence was not continuous.", errors, ref assertions);
            Check(string.Equals(beforeDigest, DigestDirectory(publishedGameRoot), StringComparison.Ordinal), "Published GameContent changed during execution.", errors, ref assertions);
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
            errors,
            "input-roundtrip",
            reportLayout,
            "input");
    }

    public static async Task<RuntimeScenarioReport> RunSaveAsync(
        string repositoryRoot,
        string fixtureId)
    {
        string fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "runtime", fixtureId);
        if (!FixtureIds.Contains(fixtureId, StringComparer.Ordinal))
        {
            return FailedSave(fixtureId, "unknown", "Fixture is not listed in the runtime manifest.");
        }

        string profile = fixtureId == "v18-core"
            ? EmueraCompatibilityProfiles.V18Compatible
            : EmueraCompatibilityProfiles.EmEeCurrent;
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();
        var evidence = new List<RuntimeScenarioAssertionEvidence>();
        int assertions = 0;
        string reportLayout = "unknown";
        string scratch = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-save", Guid.NewGuid().ToString("N"));
        string publishedGameRoot = Path.Combine(scratch, "published-game");
        string workspaceRoot = Path.Combine(scratch, "session-workspace");
        string sessionRoot = Path.Combine(workspaceRoot, "session-root");

        try
        {
            Directory.CreateDirectory(workspaceRoot);
            CopyPublishedGameContent(fixtureRoot, publishedGameRoot);
            SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(publishedGameRoot, fixtureId);
            string beforeDigest = DigestDirectory(publishedGameRoot);
            NativeSaveScenario scenario = ReadNativeSaveScenario(Path.Combine(fixtureRoot, "save-scenario.json"));
            SessionRootLayout layout = SessionRootLayoutBuilder.Build(
                publishedGameRoot,
                sessionRoot,
                manifest,
                new SessionRootCopyLimits());
            RuntimePaths paths = layout.RuntimePaths;
            reportLayout = paths.SaveLayout.ToString();
            Check(
                paths.SaveLayout == (fixtureId == "v18-core" ? RuntimeSaveLayout.Root : RuntimeSaveLayout.SavDirectory),
                "Save layout was not selected from emuera.config.",
                errors,
                ref assertions);

            var consoleA = new StructuredGameConsole();
            var fileSystemA = new LocalRuntimeFileSystem(paths);
            await using (EmueraRuntimeHost hostA = CreateHost(paths, fileSystemA, consoleA, profile))
            {
                EmueraRuntimeResult initialized = await hostA.InitializeAsync().ConfigureAwait(false);
                Check(initialized.Status == EmueraRuntimeStatus.Completed, "Save host initialization failed.", errors, ref assertions);
                if (initialized.Status != EmueraRuntimeStatus.Completed)
                {
                    errors.AddRange(initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
                    return Report("Failed", "save");
                }

                Task<EmueraRuntimeResult> run = hostA.RunAsync();
                Check(
                    SpinWait.SpinUntil(() => consoleA.CurrentPrompt is not null || run.IsCompleted, TimeSpan.FromSeconds(5)),
                    "Save host did not reach INPUT.",
                    errors,
                    ref assertions);
                ConsolePrompt? prompt = consoleA.CurrentPrompt;
                Check(prompt is not null, "Save host completed before opening a prompt.", errors, ref assertions);
                if (prompt is not null)
                {
                    Check(
                        consoleA.SubmitCurrentInput(new ConsoleInputAttempt($"save-{fixtureId}", scenario.SaveInput)).Kind ==
                        ConsoleInputResultKind.Accepted,
                        "Save input was not accepted.",
                        errors,
                        ref assertions);
                }

                EmueraRuntimeResult result = await run.ConfigureAwait(false);
                Check(result.Status == EmueraRuntimeStatus.Completed, "Save host did not complete.", errors, ref assertions);
                AddDiagnostics(result, "Save host", errors);
                string saveTranscript = RuntimeTranscriptProjector.Project(consoleA.Snapshot.VisibleNodes);
                Check(
                    saveTranscript.Contains(scenario.SaveOutput, StringComparison.Ordinal),
                    "Save completion was not visible in the runtime output.",
                    errors,
                    ref assertions);
            }

            string selectedSave = paths.SaveLayout == RuntimeSaveLayout.Root
                ? Path.Combine(paths.SessionRoot, "save00.sav")
                : Path.Combine(paths.SavDirectoryRoot, "save00.sav");
            string selectedGlobal = paths.SaveLayout == RuntimeSaveLayout.Root
                ? Path.Combine(paths.SessionRoot, "global.sav")
                : Path.Combine(paths.SavDirectoryRoot, "global.sav");
            string unselectedSave = paths.SaveLayout == RuntimeSaveLayout.Root
                ? Path.Combine(paths.SavDirectoryRoot, "save00.sav")
                : Path.Combine(paths.SessionRoot, "save00.sav");
            string unselectedGlobal = paths.SaveLayout == RuntimeSaveLayout.Root
                ? Path.Combine(paths.SavDirectoryRoot, "global.sav")
                : Path.Combine(paths.SessionRoot, "global.sav");
            Check(IsNonEmptyRegularFile(selectedSave), "The selected native save file was not created.", errors, ref assertions);
            Check(IsNonEmptyRegularFile(selectedGlobal), "The selected native global file was not created.", errors, ref assertions);
            Check(!File.Exists(unselectedSave), "The unselected save layout contains a slot file.", errors, ref assertions);
            Check(!File.Exists(unselectedGlobal), "The unselected save layout contains a global file.", errors, ref assertions);

            var consoleB = new StructuredGameConsole();
            var fileSystemB = new LocalRuntimeFileSystem(paths);
            await using (EmueraRuntimeHost hostB = CreateHost(paths, fileSystemB, consoleB, profile))
            {
                EmueraRuntimeResult initialized = await hostB.InitializeAsync().ConfigureAwait(false);
                Check(initialized.Status == EmueraRuntimeStatus.Completed, "Load host initialization failed.", errors, ref assertions);
                if (initialized.Status != EmueraRuntimeStatus.Completed)
                {
                    errors.AddRange(initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
                    return Report("Failed", "load");
                }

                Task<EmueraRuntimeResult> run = hostB.RunAsync();
                Check(
                    SpinWait.SpinUntil(() => consoleB.CurrentPrompt is not null || run.IsCompleted, TimeSpan.FromSeconds(5)),
                    "Load host did not reach INPUT.",
                    errors,
                    ref assertions);
                ConsolePrompt? prompt = consoleB.CurrentPrompt;
                Check(prompt is not null, "Load host completed before opening a prompt.", errors, ref assertions);
                if (prompt is not null)
                {
                    Check(
                        consoleB.SubmitCurrentInput(new ConsoleInputAttempt($"load-{fixtureId}", scenario.LoadInput)).Kind ==
                        ConsoleInputResultKind.Accepted,
                        "Load input was not accepted.",
                        errors,
                        ref assertions);
                }

                EmueraRuntimeResult result = await run.ConfigureAwait(false);
                Check(result.Status == EmueraRuntimeStatus.Completed, "Load host did not complete.", errors, ref assertions);
                AddDiagnostics(result, "Load host", errors);
                string transcript = RuntimeTranscriptProjector.Project(consoleB.Snapshot.VisibleNodes);
                string startupPrefix = fixtureId == "v18-core"
                    ? "V18-START\nV18-READY\nV18-INPUT"
                    : "EMEE-START\nEMEE-INPUT";
                string expected = $"{startupPrefix}\n{string.Join('\n', scenario.LoadOutputs)}";
                bool loaded = string.Equals(transcript, expected, StringComparison.Ordinal);
                Check(loaded, $"Loaded values were not visible as expected. Actual: {transcript}", errors, ref assertions);
                evidence.Add(new RuntimeScenarioAssertionEvidence("native-save-values", loaded, VerifiedByVisibleOutput: true));
            }

            Check(IsNonEmptyRegularFile(selectedSave), "The native save disappeared after Host disposal.", errors, ref assertions);
            Check(IsNonEmptyRegularFile(selectedGlobal), "The native global save disappeared after Host disposal.", errors, ref assertions);
            Check(
                string.Equals(beforeDigest, DigestDirectory(publishedGameRoot), StringComparison.Ordinal),
                "Published GameContent changed during native save/load.",
                errors,
                ref assertions);
            return Report("Completed", "save-and-restart-load");
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            return Report("Failed", "initialization");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        RuntimeScenarioReport Report(string status, string phase) => new(
            fixtureId,
            profile,
            errors.Count == 0 ? status : "Failed",
            assertions,
            stopwatch.ElapsedMilliseconds,
            RuntimeBaseline.UpstreamCommit,
            RuntimeBaseline.CloudEmueraIntegrationVersion,
            evidence,
            errors,
            reportLayout == RuntimeSaveLayout.Root.ToString() ? "save-root" :
            reportLayout == RuntimeSaveLayout.SavDirectory.ToString() ? "save-directory" : "unknown",
            reportLayout,
            phase);
    }

    private static RuntimeScenarioReport FailedSave(string fixtureId, string profile, string error) => new(
        fixtureId,
        profile,
        "Failed",
        0,
        0,
        RuntimeBaseline.UpstreamCommit,
        RuntimeBaseline.CloudEmueraIntegrationVersion,
        [],
        [error],
        fixtureId == "v18-core" ? "save-root" : "save-directory",
        "unknown",
        "initialization");

    private static void AddDiagnostics(
        EmueraRuntimeResult result,
        string context,
        List<string> errors)
    {
        foreach (EmueraRuntimeDiagnostic diagnostic in result.Diagnostics.Where(item => item.IsFatal))
        {
            errors.Add($"{context} {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static EmueraRuntimeHost CreateHost(
        RuntimePaths paths,
        LocalRuntimeFileSystem fileSystem,
        StructuredGameConsole console,
        string profile) =>
        EmueraRuntimeHost.Create(new EmueraRuntimeOptions(
            paths,
            console,
            fileSystem,
            console.Clock,
            new RuntimeImageMetadataPort(fileSystem),
            new RecordingRuntimeAudioPort(),
            profile,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10)));

    private static NativeSaveScenario ReadNativeSaveScenario(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return new NativeSaveScenario(
            root.GetProperty("saveInput").GetString() ?? throw new InvalidDataException("saveInput is missing."),
            root.GetProperty("loadInput").GetString() ?? throw new InvalidDataException("loadInput is missing."),
            root.GetProperty("saveOutput").GetString() ?? throw new InvalidDataException("saveOutput is missing."),
            root.GetProperty("loadOutputs").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
    }

    private static bool IsNonEmptyRegularFile(string path) =>
        File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        new FileInfo(path).Length > 0;

    private sealed record NativeSaveScenario(
        string SaveInput,
        string LoadInput,
        string SaveOutput,
        IReadOnlyList<string> LoadOutputs);

    private static RuntimeScenarioReport Failed(string fixtureId, string profile, string error) => new(
        fixtureId,
        profile,
        "Failed",
        0,
        0,
        RuntimeBaseline.UpstreamCommit,
        RuntimeBaseline.CloudEmueraIntegrationVersion,
        [],
        [error],
        "input-roundtrip",
        "unknown",
        "initialization");

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

    private static void CopyPublishedGameContent(string fixtureRoot, string publishedRoot)
    {
        Directory.CreateDirectory(publishedRoot);
        foreach (FileSystemInfo entry in new DirectoryInfo(fixtureRoot).EnumerateFileSystemInfos())
        {
            if (entry.Name is "scenario.json" or "save-scenario.json" or "expected-transcript.txt")
            {
                continue;
            }

            string target = Path.Combine(publishedRoot, entry.Name);
            if (entry is DirectoryInfo)
            {
                CopyDirectory(entry.FullName, target);
            }
            else if (entry is FileInfo)
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            string destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo)
            {
                CopyDirectory(entry.FullName, destination);
            }
            else if (entry is FileInfo)
            {
                File.Copy(entry.FullName, destination);
            }
        }
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
