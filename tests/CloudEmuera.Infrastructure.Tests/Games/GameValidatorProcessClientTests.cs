using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Games;

namespace CloudEmuera.Infrastructure.Tests.Games;

public sealed class GameValidatorProcessClientTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cloudemuera-validator-client-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task AcceptsOneBoundedVersionedResponse()
    {
        GameParserValidationResult result = await RunAsync("printf '%s' '{\"schemaVersion\":1,\"canActivate\":true,\"diagnostics\":[]}'");
        Assert.True(result.CanActivate);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task NormalizesSeverityFromTheBlockingBit()
    {
        GameParserValidationResult result = await RunAsync(
            "printf '%s' '{\"schemaVersion\":1,\"canActivate\":true,\"diagnostics\":[" +
            "{\"code\":\"message\",\"severity\":\"ERROR\",\"path\":null,\"message\":\"informational\",\"activationBlocking\":false}," +
            "{\"code\":\"error\",\"severity\":\"WARNING\",\"path\":null,\"message\":\"fatal\",\"activationBlocking\":true}]}'");

        Assert.False(result.CanActivate);
        Assert.Equal("WARNING", result.Diagnostics[0].Severity);
        Assert.False(result.Diagnostics[0].ActivationBlocking);
        Assert.Equal("ERROR", result.Diagnostics[1].Severity);
        Assert.True(result.Diagnostics[1].ActivationBlocking);
    }

    [Theory]
    [InlineData("printf 'not-json'", "VALIDATOR_PROTOCOL_ERROR")]
    [InlineData("exit 7", "VALIDATOR_CRASHED")]
    [InlineData("kill -9 $$", "VALIDATOR_CRASHED")]
    [InlineData("sleep 2", "VALIDATOR_TIMEOUT")]
    [InlineData("yes x | head -c 4096", "VALIDATOR_OUTPUT_LIMIT")]
    [Trait("Category", "GameLibrary")]
    public async Task ConvertsProcessAndProtocolFailuresToBlockingDiagnostics(string command, string expectedCode)
    {
        GameParserValidationResult result = await RunAsync(command, timeout: TimeSpan.FromMilliseconds(150), maximum: 1024);
        Assert.False(result.CanActivate);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode && item.ActivationBlocking);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ConvertsConfigurationPreparationFailureToBlockingDiagnostic()
    {
        GameContentPreparationResult result = await RunPreparationAsync("exit 7");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "CONFIG_GENERATOR_CRASHED" && item.ActivationBlocking);
    }

    private async Task<GameParserValidationResult> RunAsync(string command, TimeSpan? timeout = null, int maximum = 64 * 1024)
    {
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, $"validator-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\n{command}\n");
        var client = new GameValidatorProcessClient(new GameValidatorProcessOptions
        {
            ExecutablePath = "/bin/sh",
            AssemblyPath = script,
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
            MaxOutputBytes = maximum,
        });
        return await client.ValidateAsync(root);
    }

    private async Task<GameContentPreparationResult> RunPreparationAsync(string command)
    {
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, $"config-generator-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\n{command}\n");
        var client = new GameValidatorProcessClient(new GameValidatorProcessOptions
        {
            ExecutablePath = "/bin/sh",
            AssemblyPath = script,
            Timeout = TimeSpan.FromSeconds(2),
            MaxOutputBytes = 64 * 1024,
        });
        return await client.PrepareAsync(root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
