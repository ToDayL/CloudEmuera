using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

[Trait("Category", "FixtureContract")]
public sealed class RuntimeFixtureContractTests
{
    private static string FixtureRoot => RuntimeFixtureRepository.FindFixtureRoot();

    [Fact]
    public void RepositoryFixturesSatisfyManifestContract()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, result.FixtureCount);
        Assert.Equal(14, result.FileCount);
    }

    [Fact]
    public void ManifestContainsV18AndCurrentEmEeProfiles()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        RuntimeFixtureDefinition v18 = Assert.Single(result.Manifest!.Fixtures!, fixture => fixture.Id == "v18-core");
        RuntimeFixtureDefinition emEe = Assert.Single(result.Manifest.Fixtures!, fixture => fixture.Id == "em-ee-core");
        Assert.Equal("v18-compatible", v18.CompatibilityProfile);
        Assert.Equal("em-ee-current", emEe.CompatibilityProfile);
        Assert.Equal(RuntimeBaseline.UpstreamCommit, result.Manifest.RuntimeBaseline!.UpstreamCommit);
        Assert.Equal(RuntimeBaseline.CloudEmueraPatchVersion, result.Manifest.RuntimeBaseline.CloudEmueraPatchVersion);
    }

    [Fact]
    public void FixtureCoverageIncludesRequiredPhaseZeroScenarios()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        var coverage = result.Manifest!.Fixtures!
            .SelectMany(fixture => fixture.Coverage!)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "startup", "print", "variable", "function", "branch", "input",
            "html", "image", "sprite", "save-root", "save-directory"
        ];
        Assert.All(required, label => Assert.Contains(label, coverage));
    }

    [Fact]
    public void DeclaredTextEncodingsMatchFileBytes()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        var files = result.Manifest!.Fixtures!.SelectMany(fixture => fixture.Files!).ToArray();
        Assert.Contains(files, file => file.Encoding == "shift_jis");
        Assert.Contains(files, file => file.Encoding == "utf-8");
        Assert.Contains(files, file => file.Encoding == "utf-8-bom");

        RuntimeFixtureFile bomFile = Assert.Single(files, file => file.Encoding == "utf-8-bom");
        byte[] bomBytes = File.ReadAllBytes(Path.Combine(FixtureRoot, bomFile.Path!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bomBytes[..3]);

        foreach (RuntimeFixtureFile file in files.Where(file => file.Encoding == "utf-8"))
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, file.Path!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (RuntimeFixtureFile file in files.Where(file => file.Encoding == "shift_jis"))
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, file.Path!.Replace('/', Path.DirectorySeparatorChar)));
            _ = shiftJis.GetString(bytes);
        }
    }

    [Fact]
    public void SpriteImagesUseRuntimeSupportedPngFormat()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        RuntimeFixtureFile[] imageFiles = result.Manifest!.Fixtures!
            .SelectMany(fixture => fixture.Files!)
            .Where(file => file.MediaType == "image/png")
            .ToArray();
        Assert.Equal(2, imageFiles.Length);

        foreach (RuntimeFixtureFile file in imageFiles)
        {
            Assert.EndsWith(".png", file.Path!, StringComparison.Ordinal);
            byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, file.Path!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(bytes.Length >= 24);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, bytes[..8]);
            Assert.Equal("IHDR", Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
        }
    }

    [Fact]
    public void ScriptEntrypointsMatchFixedUpstreamStartupCallbacks()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (RuntimeFixtureDefinition fixture in result.Manifest!.Fixtures!)
        {
            RuntimeFixtureFile erb = Assert.Single(fixture.Files!, file =>
                file.Path!.EndsWith("/ERB/START.ERB", StringComparison.Ordinal));
            Encoding encoding = erb.Encoding == "shift_jis" ? shiftJis : new UTF8Encoding(false, true);
            string script = encoding.GetString(File.ReadAllBytes(
                Path.Combine(FixtureRoot, erb.Path!.Replace('/', Path.DirectorySeparatorChar))));
            string firstLine = script.Split('\n')[0].TrimEnd('\r');
            Assert.Equal("@SYSTEM_TITLE", firstLine);
        }
    }

    [Fact]
    public void ScenariosAndTranscriptsAreDeterministic()
    {
        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(FixtureRoot);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        foreach (RuntimeFixtureDefinition fixture in result.Manifest!.Fixtures!)
        {
            using JsonDocument scenario = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(FixtureRoot, fixture.Scenario!.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(1, scenario.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Contains(
                scenario.RootElement.GetProperty("steps").EnumerateArray(),
                step => step.GetProperty("type").GetString() == "submitInput");

            string transcript = File.ReadAllText(
                Path.Combine(FixtureRoot, fixture.ExpectedTranscript!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain('\r', transcript);
            Assert.EndsWith("\n", transcript);
            Assert.DoesNotContain("http://", transcript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", transcript, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ManifestAllowsUnknownOptionalFields()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        manifest["futureManifestField"] = true;
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        fixture["futureFixtureField"] = "ignored";
        JsonObject file = TemporaryFixtureCopy.GetFile(fixture, "v18-core/ERB/START.ERB");
        file["futureFileField"] = JsonValue.Create(17);
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(copy.Root);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }
}
