using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

[Trait("Category", "FixtureContract")]
public sealed class RuntimeFixtureValidatorTests
{
    [Fact]
    public void MissingDeclaredFileIsReported()
    {
        using var copy = new TemporaryFixtureCopy();
        File.Delete(Path.Combine(copy.Root, "v18-core", "ERB", "START.ERB"));

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("missing file", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedPayloadIsReportedAsHashMismatch()
    {
        using var copy = new TemporaryFixtureCopy();
        File.AppendAllText(Path.Combine(copy.Root, "v18-core", "ERB", "START.ERB"), "\nCHANGED\n", Encoding.ASCII);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void UnlistedPayloadIsReported()
    {
        using var copy = new TemporaryFixtureCopy();
        File.WriteAllText(Path.Combine(copy.Root, "v18-core", "ERB", "UNLISTED.ERB"), "payload\n", Encoding.ASCII);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("unlisted file", StringComparison.Ordinal));
    }

    [Fact]
    public void FixtureSourceAndLicenseAreRequired()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        fixture.Remove("source");
        fixture["license"] = "MIT";
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("source must be", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("license must be", StringComparison.Ordinal));
    }

    [Fact]
    public void FileLicenseIsRequired()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        TemporaryFixtureCopy.GetFile(fixture, "v18-core/ERB/START.ERB").Remove("license");
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("license must be", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIdsAndPathsAreRejected()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject firstFixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        JsonObject secondFixture = TemporaryFixtureCopy.GetFixture(manifest, "em-ee-core");
        secondFixture["id"] = firstFixture["id"]!.GetValue<string>();
        JsonArray secondFiles = secondFixture["files"]!.AsArray();
        secondFiles[1]!["path"] = firstFixture["files"]!.AsArray()[0]!["path"]!.GetValue<string>();
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("duplicate fixture id", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("duplicate file path", StringComparison.Ordinal) ||
            error.Contains("duplicate path", StringComparison.Ordinal));
    }

    [Fact]
    public void CaseOnlyDiskCollisionIsRejected()
    {
        using var copy = new TemporaryFixtureCopy();
        string original = Path.Combine(copy.Root, "v18-core", "ERB", "START.ERB");
        string collision = Path.Combine(copy.Root, "V18-core", "ERB", "START.ERB");
        Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
        File.Copy(original, collision);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("casing", StringComparison.Ordinal) ||
            error.Contains("duplicate path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/absolute/path.ERB")]
    [InlineData("../escape.ERB")]
    [InlineData("v18-core\\\\ERB\\\\START.ERB")]
    public void UnsafeManifestPathsAreRejected(string unsafePath)
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        TemporaryFixtureCopy.GetFile(fixture, "v18-core/ERB/START.ERB")["path"] = unsafePath;
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("relative slash-separated path", StringComparison.Ordinal) ||
            error.Contains("'.' or '..'", StringComparison.Ordinal));
    }

    [Fact]
    public void SymlinkPayloadIsRejected()
    {
        using var copy = new TemporaryFixtureCopy();
        string outside = Path.Combine(Path.GetDirectoryName(copy.Root)!, "outside-runtime-payload.ERB");
        string link = Path.Combine(copy.Root, "v18-core", "ERB", "escape.ERB");
        File.WriteAllText(outside, "outside\n", Encoding.ASCII);
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            Assert.Fail($"The symlink contract test must run on this platform: {exception.Message}");
        }

        try
        {
            RuntimeFixtureValidationResult result = Validate(copy.Root);
            Assert.Contains(result.Errors, error => error.Contains("symbolic link", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void UppercaseHashIsRejected()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        JsonObject file = TemporaryFixtureCopy.GetFile(fixture, "v18-core/ERB/START.ERB");
        file["sha256"] = file["sha256"]!.GetValue<string>().ToUpperInvariant();
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("lowercase hexadecimal", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodingDeclarationMustMatchBom()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        TemporaryFixtureCopy.GetFile(fixture, "v18-core/scenario.json")["encoding"] = "utf-8-bom";
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("requires a UTF-8 BOM", StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioCannotReferenceAnotherFixtureOrUnknownStep()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject fixture = TemporaryFixtureCopy.GetFixture(manifest, "v18-core");
        string scenarioPath = Path.Combine(copy.Root, "v18-core", "scenario.json");
        JsonObject scenario = JsonNode.Parse(File.ReadAllText(scenarioPath, Encoding.UTF8))!.AsObject();
        scenario["resources"]!.AsArray()[0] = "em-ee-core/resources/sprites.csv";
        scenario["steps"]!.AsArray().Add(new JsonObject { ["type"] = "futureRequiredStep" });
        File.WriteAllText(scenarioPath, scenario.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("resource escapes fixture", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unknown required step type", StringComparison.Ordinal));
        _ = fixture;
    }

    [Fact]
    public void RuntimeBaselineCommitAndIntegrationVersionMustMatch()
    {
        using var copy = new TemporaryFixtureCopy();
        JsonObject manifest = copy.ReadManifest();
        JsonObject baseline = manifest["runtimeBaseline"]!.AsObject();
        baseline["upstreamCommit"] = "wrong";
        baseline["cloudEmueraIntegrationVersion"] = "wrong";
        copy.WriteManifest(manifest);

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("upstreamCommit does not match", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("cloudEmueraIntegrationVersion does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateJsonPropertyIsRejected()
    {
        using var copy = new TemporaryFixtureCopy();
        string manifestPath = Path.Combine(copy.Root, "manifest.json");
        string manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
        manifest = manifest.Replace(
            "  \"schemaVersion\": 1,\n",
            "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,\n",
            StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));

        RuntimeFixtureValidationResult result = Validate(copy.Root);
        Assert.Contains(result.Errors, error => error.Contains("duplicate property", StringComparison.Ordinal));
    }

    [Fact]
    public void CliRejectsMissingChangedAndUnlicensedCopies()
    {
        using var missing = new TemporaryFixtureCopy();
        File.Delete(Path.Combine(missing.Root, "v18-core", "ERB", "START.ERB"));
        Assert.Equal(1, RunCli("--root", missing.Root));

        using var changed = new TemporaryFixtureCopy();
        File.AppendAllText(Path.Combine(changed.Root, "v18-core", "ERB", "START.ERB"), "\nCHANGED\n", Encoding.ASCII);
        Assert.Equal(1, RunCli("--root", changed.Root));

        using var unlicensed = new TemporaryFixtureCopy();
        JsonObject manifest = unlicensed.ReadManifest();
        TemporaryFixtureCopy.GetFixture(manifest, "v18-core").Remove("license");
        unlicensed.WriteManifest(manifest);
        Assert.Equal(1, RunCli("--root", unlicensed.Root));
    }

    [Fact]
    public void CliRejectsInvalidArgumentsAndAcceptsExplicitRoot()
    {
        using var copy = new TemporaryFixtureCopy();
        Assert.Equal(0, RunCli("--root", copy.Root));
        Assert.Equal(2, RunCli("--root"));
        Assert.Equal(2, RunCli("--unknown"));
        Assert.Equal(1, RunCli("--root", Path.Combine(copy.Root, "missing")));
    }

    private static RuntimeFixtureValidationResult Validate(string root)
    {
        return RuntimeFixtureValidator.Validate(root);
    }

    private static int RunCli(params string[] args)
    {
        return RuntimeFixtureCli.Run(args, TextWriter.Null, TextWriter.Null);
    }
}
