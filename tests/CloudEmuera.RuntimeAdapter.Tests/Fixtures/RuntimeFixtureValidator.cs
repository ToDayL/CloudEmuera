using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

public sealed class RuntimeFixtureValidator
{
    public const int SupportedSchemaVersion = 1;
    public const string AuthoredSource = "authored-for-cloudemuera";
    public const string SpdxLicense = "Apache-2.0";

    private static readonly HashSet<string> AllowedCompatibilityProfiles =
    [
        "v18-compatible",
        "em-ee-current"
    ];

    private static readonly HashSet<string> AllowedCoverage =
    [
        "startup",
        "print",
        "variable",
        "function",
        "branch",
        "input",
        "html",
        "image",
        "sprite",
        "save-root",
        "save-directory"
    ];

    private static readonly HashSet<string> AllowedEncodings =
    [
        "utf-8",
        "utf-8-bom",
        "shift_jis"
    ];

    private static readonly HashSet<string> AllowedTextMediaTypes =
    [
        "application/json",
        "text/csv",
        "text/plain",
        "text/x-emuera-erb",
        "text/x-emuera-erh"
    ];

    private static readonly HashSet<string> AllowedBinaryMediaTypes =
    [
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp"
    ];

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex DrivePathPattern = new("^[A-Za-z]:", RegexOptions.CultureInvariant);
    private static readonly Regex ExternalUrlPattern = new("https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AbsolutePathPattern = new("(?:^|[\\s(\"'])/(?:[^/]|$)|(?:^|[\\s(\"'])[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant);
    private static readonly Regex NonDeterministicPattern = new(
        "(?:\\b(?:timestamp|datetime|random|nonce)\\b|\\$\\{[^}]+\\}|\\[[A-Z][A-Z0-9_]+\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RuntimeFixtureValidationResult Validate(string fixtureRoot)
    {
        var result = new RuntimeFixtureValidationResult();
        string root;

        try
        {
            root = Path.GetFullPath(fixtureRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            result.AddError($"<root>: invalid fixture root: {exception.Message}");
            result.SortErrors();
            return result;
        }

        if (!Directory.Exists(root))
        {
            result.AddError($"<root>: directory does not exist: {root}");
            result.SortErrors();
            return result;
        }

        if (IsSymbolicLink(root))
        {
            result.AddError("<root>: symbolic links are not allowed for the fixture root");
            result.SortErrors();
            return result;
        }

        string manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            result.AddError("manifest.json: file is missing");
            result.SortErrors();
            return result;
        }

        if (IsSymbolicLink(manifestPath))
        {
            result.AddError("manifest.json: symbolic links are not allowed");
            result.SortErrors();
            return result;
        }

        if (!TryReadManifest(manifestPath, result, out RuntimeFixtureManifest? manifest))
        {
            result.SortErrors();
            return result;
        }

        result.Manifest = manifest;
        ValidateManifest(root, manifest!, result);
        ValidateDiskFiles(root, manifest!, result);
        result.SortErrors();
        return result;
    }

    private static bool TryReadManifest(
        string manifestPath,
        RuntimeFixtureValidationResult result,
        out RuntimeFixtureManifest? manifest)
    {
        manifest = null;
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.AddError($"manifest.json: unable to read file: {exception.Message}");
            return false;
        }

        if (!JsonDuplicatePropertyDetector.TryValidate(bytes, out string? duplicateError))
        {
            result.AddError($"manifest.json: {duplicateError}");
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<RuntimeFixtureManifest>(bytes, RuntimeFixtureJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            result.AddError($"manifest.json: invalid JSON: {exception.Message}");
            return false;
        }

        if (manifest is null)
        {
            result.AddError("manifest.json: root object is required");
            return false;
        }

        return true;
    }

    private static void ValidateManifest(
        string root,
        RuntimeFixtureManifest manifest,
        RuntimeFixtureValidationResult result)
    {
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            result.AddError($"manifest.json: schemaVersion must be {SupportedSchemaVersion}");
        }

        RuntimeFixtureRuntimeBaseline? baseline = manifest.RuntimeBaseline;
        if (baseline is null)
        {
            result.AddError("manifest.json: runtimeBaseline is required");
        }
        else
        {
            if (!string.Equals(baseline.UpstreamRepository, RuntimeBaseline.UpstreamRepository, StringComparison.Ordinal))
            {
                result.AddError("manifest.json: runtimeBaseline.upstreamRepository does not match RuntimeBaseline");
            }

            if (!string.Equals(baseline.UpstreamCommit, RuntimeBaseline.UpstreamCommit, StringComparison.Ordinal))
            {
                result.AddError("manifest.json: runtimeBaseline.upstreamCommit does not match RuntimeBaseline");
            }

            if (!string.Equals(baseline.CloudEmueraIntegrationVersion, RuntimeBaseline.CloudEmueraIntegrationVersion, StringComparison.Ordinal))
            {
                result.AddError("manifest.json: runtimeBaseline.cloudEmueraIntegrationVersion does not match RuntimeBaseline");
            }
        }

        if (manifest.Fixtures is null || manifest.Fixtures.Count == 0)
        {
            result.AddError("manifest.json: fixtures must contain at least one fixture");
            return;
        }

        var fixtureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RuntimeFixtureDefinition fixture in manifest.Fixtures)
        {
            string fixtureLabel = string.IsNullOrWhiteSpace(fixture.Id) ? "<missing-id>" : fixture.Id;
            ValidateFixtureMetadata(fixture, fixtureLabel, result, fixtureIds, gameRoots);

            string? gameRoot = fixture.GameRoot;
            string fixturePrefix = string.IsNullOrEmpty(gameRoot) ? string.Empty : gameRoot + "/";
            var fixturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RuntimeFixtureFile> filesByPath = new(StringComparer.OrdinalIgnoreCase);

            if (fixture.Files is null || fixture.Files.Count == 0)
            {
                result.AddError($"{fixtureLabel}: files must contain at least one payload");
            }
            else
            {
                foreach (RuntimeFixtureFile file in fixture.Files)
                {
                    string pathLabel = string.IsNullOrWhiteSpace(file.Path) ? "<missing-path>" : file.Path;
                    bool pathValid = ValidateRelativePath(pathLabel, $"{fixtureLabel}.files", result);
                    if (pathValid && !string.IsNullOrEmpty(fixturePrefix) &&
                        !pathLabel.StartsWith(fixturePrefix, StringComparison.Ordinal))
                    {
                        result.AddError($"{fixtureLabel}.files: path must stay under gameRoot: {pathLabel}");
                    }

                    if (!fixturePaths.Add(pathLabel))
                    {
                        result.AddError($"{fixtureLabel}.files: duplicate path or case collision: {pathLabel}");
                    }

                    if (!declaredPaths.Add(pathLabel))
                    {
                        result.AddError($"manifest.json: duplicate file path across fixtures: {pathLabel}");
                    }
                    else
                    {
                        filesByPath[pathLabel] = file;
                    }

                    ValidateFileMetadata(file, $"{fixtureLabel}.files[{pathLabel}]", result);
                }
            }

            ValidateFixtureReferences(root, fixture, fixtureLabel, fixturePrefix, filesByPath, result);
        }
    }

    private static void ValidateFixtureMetadata(
        RuntimeFixtureDefinition fixture,
        string fixtureLabel,
        RuntimeFixtureValidationResult result,
        HashSet<string> fixtureIds,
        HashSet<string> gameRoots)
    {
        if (string.IsNullOrWhiteSpace(fixture.Id))
        {
            result.AddError($"{fixtureLabel}: id is required");
        }
        else
        {
            if (!Regex.IsMatch(fixture.Id, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant))
            {
                result.AddError($"{fixtureLabel}: id must use lowercase ASCII kebab-case");
            }

            if (!fixtureIds.Add(fixture.Id))
            {
                result.AddError($"{fixtureLabel}: duplicate fixture id or case collision");
            }
        }

        if (string.IsNullOrWhiteSpace(fixture.CompatibilityProfile) ||
            !AllowedCompatibilityProfiles.Contains(fixture.CompatibilityProfile))
        {
            result.AddError($"{fixtureLabel}: compatibilityProfile is unknown or missing");
        }

        ValidateSourceAndLicense(fixture.Source, fixture.License, fixtureLabel, result);

        if (!ValidateRelativePath(fixture.GameRoot, $"{fixtureLabel}.gameRoot", result))
        {
            return;
        }

        if (fixture.GameRoot!.Contains('/'))
        {
            result.AddError($"{fixtureLabel}: gameRoot must be a single directory name");
        }

        if (!gameRoots.Add(fixture.GameRoot))
        {
            result.AddError($"{fixtureLabel}: duplicate gameRoot or case collision");
        }

        if (fixture.Coverage is null || fixture.Coverage.Count == 0)
        {
            result.AddError($"{fixtureLabel}: coverage must contain at least one label");
        }
        else
        {
            var coverage = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? label in fixture.Coverage)
            {
                if (string.IsNullOrWhiteSpace(label) || !AllowedCoverage.Contains(label))
                {
                    result.AddError($"{fixtureLabel}: unknown coverage label: {label ?? "<null>"}");
                }
                else if (!coverage.Add(label))
                {
                    result.AddError($"{fixtureLabel}: duplicate coverage label: {label}");
                }
            }
        }
    }

    private static void ValidateSourceAndLicense(
        string? source,
        string? license,
        string label,
        RuntimeFixtureValidationResult result)
    {
        if (!string.Equals(source, AuthoredSource, StringComparison.Ordinal))
        {
            result.AddError($"{label}: source must be {AuthoredSource}");
        }

        if (!string.Equals(license, SpdxLicense, StringComparison.Ordinal))
        {
            result.AddError($"{label}: license must be {SpdxLicense}");
        }
    }

    private static void ValidateFileMetadata(
        RuntimeFixtureFile file,
        string label,
        RuntimeFixtureValidationResult result)
    {
        ValidateSourceAndLicense(file.Source, file.License, label, result);

        if (!Sha256Pattern.IsMatch(file.Sha256 ?? string.Empty))
        {
            result.AddError($"{label}: sha256 must be 64 lowercase hexadecimal characters");
        }

        string mediaType = file.MediaType ?? string.Empty;
        bool isText = AllowedTextMediaTypes.Contains(mediaType);
        bool isBinary = AllowedBinaryMediaTypes.Contains(mediaType);
        if (!isText && !isBinary)
        {
            result.AddError($"{label}: unsupported or missing mediaType: {mediaType}");
        }

        if (isText)
        {
            if (string.IsNullOrWhiteSpace(file.Encoding) || !AllowedEncodings.Contains(file.Encoding))
            {
                result.AddError($"{label}: text payload must declare utf-8, utf-8-bom or shift_jis");
            }
        }
        else if (!string.IsNullOrEmpty(file.Encoding))
        {
            result.AddError($"{label}: binary payload must not declare a text encoding");
        }

        string extension = file.Path is null ? string.Empty : Path.GetExtension(file.Path);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError($"{label}: executable payloads are forbidden");
        }
    }

    private static void ValidateFixtureReferences(
        string root,
        RuntimeFixtureDefinition fixture,
        string fixtureLabel,
        string fixturePrefix,
        Dictionary<string, RuntimeFixtureFile> filesByPath,
        RuntimeFixtureValidationResult result)
    {
        string? scenarioPath = fixture.Scenario;
        string? transcriptPath = fixture.ExpectedTranscript;
        string? saveScenarioPath = fixture.SaveScenario;
        bool scenarioPathValid = ValidateFixtureReferencePath(scenarioPath, "scenario", fixtureLabel, fixturePrefix, filesByPath, result);
        bool transcriptPathValid = ValidateFixtureReferencePath(transcriptPath, "expectedTranscript", fixtureLabel, fixturePrefix, filesByPath, result);
        bool saveScenarioPathValid = ValidateFixtureReferencePath(saveScenarioPath, "saveScenario", fixtureLabel, fixturePrefix, filesByPath, result);

        if (scenarioPathValid && transcriptPathValid && string.Equals(scenarioPath, transcriptPath, StringComparison.OrdinalIgnoreCase))
        {
            result.AddError($"{fixtureLabel}: scenario and expectedTranscript must be different files");
        }

        if (scenarioPathValid)
        {
            RuntimeFixtureFile scenarioFile = filesByPath[scenarioPath!];
            if (!string.Equals(scenarioFile.MediaType, "application/json", StringComparison.Ordinal) ||
                !string.Equals(scenarioFile.Encoding, "utf-8", StringComparison.Ordinal))
            {
                result.AddError($"{fixtureLabel}: scenario must be application/json encoded as utf-8");
            }

            string scenarioAbsolutePath = ResolvePath(root, scenarioPath!);
            if (TryReadText(scenarioAbsolutePath, scenarioFile.Encoding, out string? scenarioText, out string? scenarioError))
            {
                ValidateScenario(scenarioText!, fixture, fixtureLabel, fixturePrefix, filesByPath, result);
            }
            else
            {
                result.AddError($"{fixtureLabel}.scenario: {scenarioError}");
            }
        }

        if (transcriptPathValid)
        {
            RuntimeFixtureFile transcriptFile = filesByPath[transcriptPath!];
            if (!string.Equals(transcriptFile.MediaType, "text/plain", StringComparison.Ordinal) ||
                !string.Equals(transcriptFile.Encoding, "utf-8", StringComparison.Ordinal))
            {
                result.AddError($"{fixtureLabel}: expectedTranscript must be text/plain encoded as utf-8");
            }

            string transcriptAbsolutePath = ResolvePath(root, transcriptPath!);
            if (TryReadText(transcriptAbsolutePath, transcriptFile.Encoding, out string? transcript, out string? transcriptError))
            {
                ValidateTranscript(transcript!, fixtureLabel, result);
            }
            else
            {
                result.AddError($"{fixtureLabel}.expectedTranscript: {transcriptError}");
            }
        }

        if (saveScenarioPathValid)
        {
            RuntimeFixtureFile saveScenarioFile = filesByPath[saveScenarioPath!];
            if (!string.Equals(saveScenarioFile.MediaType, "application/json", StringComparison.Ordinal) ||
                !string.Equals(saveScenarioFile.Encoding, "utf-8", StringComparison.Ordinal))
            {
                result.AddError($"{fixtureLabel}: saveScenario must be application/json encoded as utf-8");
            }

            string saveScenarioAbsolutePath = ResolvePath(root, saveScenarioPath!);
            if (TryReadText(saveScenarioAbsolutePath, saveScenarioFile.Encoding, out string? saveScenarioText, out string? saveScenarioError))
            {
                ValidateSaveScenario(saveScenarioText!, fixture, fixtureLabel, result);
            }
            else
            {
                result.AddError($"{fixtureLabel}.saveScenario: {saveScenarioError}");
            }
        }
    }

    private static bool ValidateFixtureReferencePath(
        string? path,
        string field,
        string fixtureLabel,
        string fixturePrefix,
        Dictionary<string, RuntimeFixtureFile> filesByPath,
        RuntimeFixtureValidationResult result)
    {
        if (!ValidateRelativePath(path, $"{fixtureLabel}.{field}", result))
        {
            return false;
        }

        if (string.IsNullOrEmpty(fixturePrefix) || !path!.StartsWith(fixturePrefix, StringComparison.Ordinal))
        {
            result.AddError($"{fixtureLabel}.{field}: path must stay under gameRoot: {path}");
            return false;
        }

        if (!filesByPath.ContainsKey(path))
        {
            result.AddError($"{fixtureLabel}.{field}: referenced file is not declared: {path}");
            return false;
        }

        return true;
    }

    private static void ValidateScenario(
        string scenarioText,
        RuntimeFixtureDefinition fixture,
        string fixtureLabel,
        string fixturePrefix,
        Dictionary<string, RuntimeFixtureFile> filesByPath,
        RuntimeFixtureValidationResult result)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(scenarioText);
        if (!JsonDuplicatePropertyDetector.TryValidate(bytes, out string? duplicateError))
        {
            result.AddError($"{fixtureLabel}.scenario: {duplicateError}");
            return;
        }

        RuntimeFixtureScenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<RuntimeFixtureScenario>(scenarioText, RuntimeFixtureJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            result.AddError($"{fixtureLabel}.scenario: invalid JSON: {exception.Message}");
            return;
        }

        if (scenario is null)
        {
            result.AddError($"{fixtureLabel}.scenario: root object is required");
            return;
        }

        if (scenario.SchemaVersion != SupportedSchemaVersion)
        {
            result.AddError($"{fixtureLabel}.scenario: schemaVersion must be {SupportedSchemaVersion}");
        }

        if (!string.Equals(scenario.FixtureId, fixture.Id, StringComparison.Ordinal))
        {
            result.AddError($"{fixtureLabel}.scenario: fixtureId must equal fixture id");
        }

        if (scenario.Resources is null || scenario.Resources.Count == 0)
        {
            result.AddError($"{fixtureLabel}.scenario: resources must list at least one fixture asset");
        }
        else
        {
            var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? resource in scenario.Resources)
            {
                if (!ValidateRelativePath(resource, $"{fixtureLabel}.scenario.resources", result))
                {
                    continue;
                }

                if (!resource!.StartsWith(fixturePrefix, StringComparison.Ordinal))
                {
                    result.AddError($"{fixtureLabel}.scenario: resource escapes fixture: {resource}");
                }
                else if (!filesByPath.ContainsKey(resource))
                {
                    result.AddError($"{fixtureLabel}.scenario: resource is not declared: {resource}");
                }

                if (!resourcePaths.Add(resource))
                {
                    result.AddError($"{fixtureLabel}.scenario: duplicate resource path: {resource}");
                }
            }
        }

        if (scenario.Steps is null || scenario.Steps.Count == 0)
        {
            result.AddError($"{fixtureLabel}.scenario: steps must contain at least one step");
            return;
        }

        bool inputSeen = false;
        bool observableAfterInput = false;
        foreach (RuntimeFixtureScenarioStep step in scenario.Steps)
        {
            string type = step.Type ?? string.Empty;
            switch (type)
            {
                case "expectOutput":
                    ValidateStableText(step.Text, $"{fixtureLabel}.scenario.expectOutput", result);
                    if (inputSeen)
                    {
                        observableAfterInput = true;
                    }

                    break;
                case "submitInput":
                    if (!string.Equals(step.InputKind, "integer", StringComparison.Ordinal) &&
                        !string.Equals(step.InputKind, "string", StringComparison.Ordinal))
                    {
                        result.AddError($"{fixtureLabel}.scenario.submitInput: inputKind must be integer or string");
                    }

                    if (string.IsNullOrWhiteSpace(step.Value))
                    {
                        result.AddError($"{fixtureLabel}.scenario.submitInput: value is required");
                    }
                    else if (string.Equals(step.InputKind, "integer", StringComparison.Ordinal) &&
                             !long.TryParse(step.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        result.AddError($"{fixtureLabel}.scenario.submitInput: integer value is invalid");
                    }

                    inputSeen = true;
                    break;
                case "expectVariable":
                    if (string.IsNullOrWhiteSpace(step.Name) || string.IsNullOrWhiteSpace(step.Value))
                    {
                        result.AddError($"{fixtureLabel}.scenario.expectVariable: name and value are required");
                    }
                    else
                    {
                        ValidateStableText(step.Name, $"{fixtureLabel}.scenario.expectVariable.name", result);
                        ValidateStableText(step.Value, $"{fixtureLabel}.scenario.expectVariable.value", result);
                    }

                    if (inputSeen)
                    {
                        observableAfterInput = true;
                    }

                    break;
                case "expectSavePath":
                    ValidateSavePath(step.Path, fixtureLabel, fixture, result);
                    if (inputSeen)
                    {
                        observableAfterInput = true;
                    }

                    break;
                case "expectDiagnostic":
                    if (string.IsNullOrWhiteSpace(step.Code) || string.IsNullOrWhiteSpace(step.Expected))
                    {
                        result.AddError($"{fixtureLabel}.scenario.expectDiagnostic: code and expected are required");
                    }
                    else
                    {
                        ValidateStableText(step.Code, $"{fixtureLabel}.scenario.expectDiagnostic.code", result);
                        ValidateStableText(step.Expected, $"{fixtureLabel}.scenario.expectDiagnostic.expected", result);
                    }

                    if (inputSeen)
                    {
                        observableAfterInput = true;
                    }

                    break;
                default:
                    result.AddError($"{fixtureLabel}.scenario: unknown required step type: {type}");
                    break;
            }
        }

        if (!inputSeen)
        {
            result.AddError($"{fixtureLabel}.scenario: at least one submitInput step is required");
        }

        if (!observableAfterInput)
        {
            result.AddError($"{fixtureLabel}.scenario: an observable output, variable, save path or diagnostic is required after input");
        }
    }

    private static void ValidateSaveScenario(
        string saveScenarioText,
        RuntimeFixtureDefinition fixture,
        string fixtureLabel,
        RuntimeFixtureValidationResult result)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(saveScenarioText);
        if (!JsonDuplicatePropertyDetector.TryValidate(bytes, out string? duplicateError))
        {
            result.AddError($"{fixtureLabel}.saveScenario: {duplicateError}");
            return;
        }

        RuntimeFixtureSaveScenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<RuntimeFixtureSaveScenario>(
                saveScenarioText,
                RuntimeFixtureJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            result.AddError($"{fixtureLabel}.saveScenario: invalid JSON: {exception.Message}");
            return;
        }

        if (scenario is null)
        {
            result.AddError($"{fixtureLabel}.saveScenario: root object is required");
            return;
        }

        if (scenario.SchemaVersion != SupportedSchemaVersion)
        {
            result.AddError($"{fixtureLabel}.saveScenario: schemaVersion must be {SupportedSchemaVersion}");
        }

        if (!string.Equals(scenario.FixtureId, fixture.Id, StringComparison.Ordinal))
        {
            result.AddError($"{fixtureLabel}.saveScenario: fixtureId must equal fixture id");
        }

        if (string.IsNullOrWhiteSpace(scenario.SaveInput) ||
            !long.TryParse(
                scenario.SaveInput,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            result.AddError($"{fixtureLabel}.saveScenario: saveInput must be an integer string");
        }

        if (string.IsNullOrWhiteSpace(scenario.LoadInput) ||
            !long.TryParse(
                scenario.LoadInput,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            result.AddError($"{fixtureLabel}.saveScenario: loadInput must be an integer string");
        }

        ValidateStableText(scenario.SaveOutput, $"{fixtureLabel}.saveScenario.saveOutput", result);
        if (scenario.LoadOutputs is null || scenario.LoadOutputs.Count == 0)
        {
            result.AddError($"{fixtureLabel}.saveScenario: loadOutputs must contain at least one value");
        }
        else
        {
            foreach (string? output in scenario.LoadOutputs)
            {
                ValidateStableText(output, $"{fixtureLabel}.saveScenario.loadOutputs", result);
            }
        }
    }

    private static void ValidateSavePath(
        string? path,
        string fixtureLabel,
        RuntimeFixtureDefinition fixture,
        RuntimeFixtureValidationResult result)
    {
        if (!ValidateRelativePath(path, $"{fixtureLabel}.scenario.expectSavePath", result))
        {
            return;
        }

        bool isRootSave = !path!.Contains('/') &&
            (path.Equals("global.sav", StringComparison.OrdinalIgnoreCase) ||
             (path.StartsWith("save", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)));
        bool isDirectorySave = path.StartsWith("sav/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".sav", StringComparison.OrdinalIgnoreCase);
        if (!isRootSave && !isDirectorySave)
        {
            result.AddError($"{fixtureLabel}.scenario.expectSavePath: unsupported native save path: {path}");
            return;
        }

        if (string.Equals(fixture.CompatibilityProfile, "v18-compatible", StringComparison.Ordinal) && !isRootSave)
        {
            result.AddError($"{fixtureLabel}.scenario.expectSavePath: v18 fixture must use root save semantics");
        }

        if (string.Equals(fixture.CompatibilityProfile, "em-ee-current", StringComparison.Ordinal) && !isDirectorySave)
        {
            result.AddError($"{fixtureLabel}.scenario.expectSavePath: em-ee fixture must use sav/ semantics");
        }
    }

    private static void ValidateTranscript(
        string transcript,
        string fixtureLabel,
        RuntimeFixtureValidationResult result)
    {
        if (transcript.Length == 0 || !transcript.EndsWith('\n'))
        {
            result.AddError($"{fixtureLabel}.expectedTranscript: transcript must end with exactly a final LF");
        }

        if (transcript.Contains('\r'))
        {
            result.AddError($"{fixtureLabel}.expectedTranscript: transcript must not contain CR");
        }

        ValidateStableText(transcript, $"{fixtureLabel}.expectedTranscript", result);
    }

    private static void ValidateStableText(
        string? text,
        string label,
        RuntimeFixtureValidationResult result)
    {
        if (text is null)
        {
            result.AddError($"{label}: text is required");
            return;
        }

        if (text.Contains('\r'))
        {
            result.AddError($"{label}: text must use LF only");
        }

        if (ExternalUrlPattern.IsMatch(text))
        {
            result.AddError($"{label}: external URLs are forbidden");
        }

        if (AbsolutePathPattern.IsMatch(text) || text.Contains('\\'))
        {
            result.AddError($"{label}: absolute or machine-specific paths are forbidden");
        }

        if (NonDeterministicPattern.IsMatch(text))
        {
            result.AddError($"{label}: timestamps, random values and placeholders are forbidden");
        }
    }

    private static void ValidateDiskFiles(
        string root,
        RuntimeFixtureManifest manifest,
        RuntimeFixtureValidationResult result)
    {
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Fixtures is not null)
        {
            foreach (RuntimeFixtureDefinition fixture in manifest.Fixtures)
            {
                if (fixture.Files is null)
                {
                    continue;
                }

                foreach (RuntimeFixtureFile file in fixture.Files)
                {
                    if (!string.IsNullOrWhiteSpace(file.Path))
                    {
                        declaredPaths.Add(file.Path);
                    }
                }
            }
        }

        var diskPaths = new List<string>();
        WalkDirectory(root, root, diskPaths, result);
        var diskPathSet = new HashSet<string>(StringComparer.Ordinal);
        var diskPathIgnoreCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in diskPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!diskPathSet.Add(path))
            {
                continue;
            }

            if (!diskPathIgnoreCase.Add(path))
            {
                result.AddError($"{path}: duplicate path differing only by case");
            }

            if (!declaredPaths.Contains(path))
            {
                if (declaredPaths.Any(declared => string.Equals(declared, path, StringComparison.OrdinalIgnoreCase)))
                {
                    result.AddError($"{path}: path casing differs from manifest");
                }
                else
                {
                    result.AddError($"{path}: unlisted file");
                }
            }
        }

        foreach (string declaredPath in declaredPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!diskPathSet.Contains(declaredPath))
            {
                if (diskPathIgnoreCase.Contains(declaredPath))
                {
                    result.AddError($"{declaredPath}: path casing differs from disk");
                }
                else
                {
                    result.AddError($"{declaredPath}: missing file");
                }
            }
        }

        if (manifest.Fixtures is null)
        {
            return;
        }

        foreach (RuntimeFixtureDefinition fixture in manifest.Fixtures)
        {
            if (fixture.Files is null)
            {
                continue;
            }

            foreach (RuntimeFixtureFile file in fixture.Files)
            {
                if (!TryValidateManifestPathForDisk(file.Path, root, result, out string? absolutePath))
                {
                    continue;
                }

                if (!File.Exists(absolutePath))
                {
                    continue;
                }

                if (IsSymbolicLink(absolutePath))
                {
                    result.AddError($"{file.Path}: symbolic links are not allowed");
                    continue;
                }

                if (!TryComputeSha256(absolutePath, out string? actualHash, out string? hashError))
                {
                    result.AddError($"{file.Path}: unable to hash file: {hashError}");
                    continue;
                }

                if (!string.Equals(file.Sha256, actualHash, StringComparison.Ordinal))
                {
                    result.AddError($"{file.Path}: SHA-256 mismatch (manifest {file.Sha256 ?? "<missing>"}, actual {actualHash})");
                }

                if (AllowedTextMediaTypes.Contains(file.MediaType ?? string.Empty))
                {
                    if (TryReadText(absolutePath, file.Encoding, out string? text, out string? textError))
                    {
                        if (ExternalUrlPattern.IsMatch(text!))
                        {
                            result.AddError($"{file.Path}: external URLs are forbidden");
                        }
                    }
                    else
                    {
                        result.AddError($"{file.Path}: {textError}");
                    }
                }
                else if (AllowedBinaryMediaTypes.Contains(file.MediaType ?? string.Empty))
                {
                    ValidateBinaryPayload(absolutePath, file, result);
                }
            }
        }
    }

    private static void ValidateBinaryPayload(
        string absolutePath,
        RuntimeFixtureFile file,
        RuntimeFixtureValidationResult result)
    {
        string extension = Path.GetExtension(file.Path ?? string.Empty).ToLowerInvariant();
        string? expectedMediaType = extension switch
        {
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };

        if (!string.Equals(expectedMediaType, file.MediaType, StringComparison.Ordinal))
        {
            result.AddError($"{file.Path}: mediaType {file.MediaType ?? "<missing>"} does not match image extension {extension}");
            return;
        }

        byte[] header = new byte[32];
        int bytesRead;
        try
        {
            using FileStream stream = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytesRead = stream.Read(header, 0, header.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.AddError($"{file.Path}: unable to read image header: {exception.Message}");
            return;
        }

        ReadOnlySpan<byte> bytes = header.AsSpan(0, bytesRead);
        bool valid = file.MediaType switch
        {
            "image/gif" => HasPrefix(bytes, "GIF87a"u8) || HasPrefix(bytes, "GIF89a"u8),
            "image/jpeg" => HasPrefix(bytes, [0xff, 0xd8, 0xff]),
            "image/png" => IsPng(bytes),
            "image/webp" => bytes.Length >= 12 && HasPrefix(bytes, "RIFF"u8) && HasPrefix(bytes[8..], "WEBP"u8),
            _ => false
        };

        if (!valid)
        {
            result.AddError($"{file.Path}: invalid {file.MediaType} image signature");
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 24 &&
            HasPrefix(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) == 13 &&
            HasPrefix(bytes[12..], "IHDR"u8) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) > 0 &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) > 0;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix)
    {
        return bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
    }

    private static void WalkDirectory(
        string root,
        string current,
        List<string> files,
        RuntimeFixtureValidationResult result)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(current)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.AddError($"{Path.GetRelativePath(root, current).Replace(Path.DirectorySeparatorChar, '/')}: unable to enumerate directory: {exception.Message}");
            return;
        }

        foreach (string entry in entries)
        {
            string relativePath = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            if (IsSymbolicLink(entry))
            {
                result.AddError($"{relativePath}: symbolic links are not allowed");
                continue;
            }

            bool isDirectory = Directory.Exists(entry);
            if (isDirectory)
            {
                WalkDirectory(root, entry, files, result);
                continue;
            }

            if (relativePath.Equals("manifest.json", StringComparison.Ordinal) ||
                IsDocumentationFile(relativePath))
            {
                continue;
            }

            files.Add(relativePath);
        }
    }

    private static bool IsDocumentationFile(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateManifestPathForDisk(
        string? path,
        string root,
        RuntimeFixtureValidationResult result,
        out string? absolutePath)
    {
        absolutePath = null;
        if (!ValidateRelativePath(path, "manifest file", result))
        {
            return false;
        }

        absolutePath = ResolvePath(root, path!);
        if (!IsWithinRoot(root, absolutePath))
        {
            result.AddError($"{path}: resolved path escapes fixture root");
            absolutePath = null;
            return false;
        }

        string current = root;
        foreach (string segment in path!.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (IsSymbolicLink(current))
            {
                result.AddError($"{path}: symbolic link in path is not allowed");
                return false;
            }
        }

        return true;
    }

    private static bool ValidateRelativePath(
        string? path,
        string label,
        RuntimeFixtureValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            result.AddError($"{label}: path is required");
            return false;
        }

        if (path.Contains('\\') || path.Contains('\0') ||
            path.StartsWith('/') || Path.IsPathRooted(path) || DrivePathPattern.IsMatch(path))
        {
            result.AddError($"{label}: path must be a relative slash-separated path: {path}");
            return false;
        }

        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            result.AddError($"{label}: path contains an empty, '.' or '..' segment: {path}");
            return false;
        }

        return true;
    }

    private static string ResolvePath(string root, string relativePath)
    {
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, platformPath));
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal) ||
            string.Equals(normalizedCandidate, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
    }

    private static bool TryReadText(
        string path,
        string? encodingName,
        out string? text,
        out string? error)
    {
        text = null;
        error = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"unable to read text: {exception.Message}";
            return false;
        }

        try
        {
            if (string.Equals(encodingName, "utf-8", StringComparison.Ordinal))
            {
                if (HasUtf8Bom(bytes))
                {
                    error = "utf-8 declaration must not contain a BOM";
                    return false;
                }

                text = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }

            if (string.Equals(encodingName, "utf-8-bom", StringComparison.Ordinal))
            {
                if (!HasUtf8Bom(bytes))
                {
                    error = "utf-8-bom declaration requires a UTF-8 BOM";
                    return false;
                }

                text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
                return true;
            }

            if (string.Equals(encodingName, "shift_jis", StringComparison.Ordinal))
            {
                if (HasUtf8Bom(bytes))
                {
                    error = "shift_jis payload must not contain a UTF-8 BOM";
                    return false;
                }

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Encoding shiftJis = Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                text = shiftJis.GetString(bytes);
                return true;
            }

            error = $"unsupported text encoding: {encodingName ?? "<missing>"}";
            return false;
        }
        catch (DecoderFallbackException exception)
        {
            error = $"invalid {encodingName} bytes: {exception.Message}";
            return false;
        }
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static bool TryComputeSha256(string path, out string? hash, out string? error)
    {
        hash = null;
        error = null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static class JsonDuplicatePropertyDetector
    {
        public static bool TryValidate(ReadOnlySpan<byte> bytes, out string? error)
        {
            error = null;
            var objects = new Stack<HashSet<string>>(capacity: 8);
            try
            {
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            objects.Push(new HashSet<string>(StringComparer.Ordinal));
                            break;
                        case JsonTokenType.EndObject:
                            if (objects.Count == 0)
                            {
                                error = "invalid object nesting";
                                return false;
                            }

                            objects.Pop();
                            break;
                        case JsonTokenType.PropertyName:
                            if (objects.Count == 0)
                            {
                                error = "property name is outside an object";
                                return false;
                            }

                            string propertyName = reader.GetString() ?? string.Empty;
                            if (!objects.Peek().Add(propertyName))
                            {
                                error = $"duplicate property: {propertyName}";
                                return false;
                            }

                            break;
                    }
                }

                if (objects.Count != 0)
                {
                    error = "unterminated JSON object";
                    return false;
                }

                return true;
            }
            catch (JsonException exception)
            {
                error = $"invalid JSON: {exception.Message}";
                return false;
            }
        }
    }
}
