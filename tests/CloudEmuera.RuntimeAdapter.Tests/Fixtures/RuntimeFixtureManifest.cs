using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

public sealed class RuntimeFixtureManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("runtimeBaseline")]
    public RuntimeFixtureRuntimeBaseline? RuntimeBaseline { get; set; }

    [JsonPropertyName("fixtures")]
    public List<RuntimeFixtureDefinition>? Fixtures { get; set; }
}

public sealed class RuntimeFixtureRuntimeBaseline
{
    [JsonPropertyName("upstreamRepository")]
    public string? UpstreamRepository { get; set; }

    [JsonPropertyName("upstreamCommit")]
    public string? UpstreamCommit { get; set; }

    [JsonPropertyName("cloudEmueraPatchVersion")]
    public string? CloudEmueraPatchVersion { get; set; }
}

public sealed class RuntimeFixtureDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("compatibilityProfile")]
    public string? CompatibilityProfile { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("gameRoot")]
    public string? GameRoot { get; set; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; set; }

    [JsonPropertyName("expectedTranscript")]
    public string? ExpectedTranscript { get; set; }

    [JsonPropertyName("coverage")]
    public List<string>? Coverage { get; set; }

    [JsonPropertyName("files")]
    public List<RuntimeFixtureFile>? Files { get; set; }
}

public sealed class RuntimeFixtureFile
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }
}

public sealed class RuntimeFixtureScenario
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("fixtureId")]
    public string? FixtureId { get; set; }

    [JsonPropertyName("resources")]
    public List<string>? Resources { get; set; }

    [JsonPropertyName("steps")]
    public List<RuntimeFixtureScenarioStep>? Steps { get; set; }
}

public sealed class RuntimeFixtureScenarioStep
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inputKind")]
    public string? InputKind { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("expected")]
    public string? Expected { get; set; }
}

public sealed class RuntimeFixtureValidationResult
{
    private readonly List<string> errors = [];

    public RuntimeFixtureManifest? Manifest { get; internal set; }

    public IReadOnlyList<string> Errors => errors;

    public bool IsValid => errors.Count == 0;

    public int FixtureCount => Manifest?.Fixtures?.Count ?? 0;

    public int FileCount => Manifest?.Fixtures?.Where(f => f.Files is not null).Sum(f => f.Files!.Count) ?? 0;

    internal void AddError(string message)
    {
        errors.Add(message);
    }

    internal void SortErrors()
    {
        errors.Sort(StringComparer.Ordinal);
    }
}

public static class RuntimeFixtureJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
}
