using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Games;

namespace CloudEmuera.Infrastructure.Tests.Games;

public sealed class GameValidatorProcessIntegrationTests
{
    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task OneShotValidatorLoadsPinnedEmueraParserWithoutRunningGameLoop()
    {
        string root = Directory.CreateTempSubdirectory("cloudemuera-real-validator-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "CSV"));
            Directory.CreateDirectory(Path.Combine(root, "ERB"));
            Directory.CreateDirectory(Path.Combine(root, "ReadMe"));
            await File.WriteAllTextAsync(Path.Combine(root, "CSV", "GAMEBASE.CSV"), "コード,验证器测试\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ERB", "START.ERB"), "@SYSTEM_TITLE\nPRINTL 加载测试\nINPUT\nQUIT\n");
            await File.WriteAllTextAsync(Path.Combine(root, "emuera.config"), "Use sav folder:NO\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ReadMe", "eraSQN\u0083p\u0083b\u0083`.txt"), "not runtime content");
            string assembly = Path.Combine(FindRepositoryRoot(), "src", "CloudEmuera.Validator", "bin", "Release", "net10.0", "CloudEmuera.Validator.dll");
            var client = new GameValidatorProcessClient(new GameValidatorProcessOptions
            {
                ExecutablePath = "dotnet",
                AssemblyPath = assembly,
                Timeout = TimeSpan.FromSeconds(20),
            });

            GameParserValidationResult result = await client.ValidateAsync(root);

            Assert.True(result.CanActivate, string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task OneShotValidatorKeepsUpstreamLoadingReportNonBlocking()
    {
        string root = Directory.CreateTempSubdirectory("cloudemuera-real-validator-report-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "CSV"));
            Directory.CreateDirectory(Path.Combine(root, "ERB"));
            await File.WriteAllTextAsync(Path.Combine(root, "CSV", "GAMEBASE.CSV"), "コード,报告测试\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ERB", "START.ERB"), "@SYSTEM_TITLE\nQUIT\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "emuera.config"),
                "Use sav folder:NO\nロード時にレポートを表示する:YES\n");

            GameParserValidationResult result = await CreateClient().ValidateAsync(root);

            Assert.True(result.CanActivate, string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.Contains(result.Diagnostics, item =>
                item.Code == "RUNTIME_MESSAGE" &&
                item.Severity == "WARNING" &&
                !item.ActivationBlocking);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task OneShotValidatorReadsSettingJsonFromAnIsolatedSessionRoot()
    {
        string root = Directory.CreateTempSubdirectory("cloudemuera-real-validator-setting-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "CSV"));
            Directory.CreateDirectory(Path.Combine(root, "ERB"));
            await File.WriteAllTextAsync(Path.Combine(root, "CSV", "GAMEBASE.CSV"), "代码,setting测试\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "ERB", "START.ERB"),
                "@SYSTEM_TITLE\nVARI IDX\nQUIT\n");
            await File.WriteAllTextAsync(Path.Combine(root, "emuera.config"), "Use sav folder:NO\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "setting.json"),
                "{\"UseScopedVariableInstruction\":true}");

            GameParserValidationResult result = await CreateClient().ValidateAsync(root);

            Assert.True(result.CanActivate, string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.Empty(result.Diagnostics);
            Assert.Equal("{\"UseScopedVariableInstruction\":true}", await File.ReadAllTextAsync(Path.Combine(root, "setting.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task OneShotValidatorCreatesMissingSettingJsonOutsideInputRoot()
    {
        string root = Directory.CreateTempSubdirectory("cloudemuera-real-validator-setting-default-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "CSV"));
            Directory.CreateDirectory(Path.Combine(root, "ERB"));
            await File.WriteAllTextAsync(Path.Combine(root, "CSV", "GAMEBASE.CSV"), "代码,setting默认测试\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ERB", "START.ERB"), "@SYSTEM_TITLE\nQUIT\n");
            await File.WriteAllTextAsync(Path.Combine(root, "emuera.config"), "Use sav folder:NO\n");

            GameParserValidationResult result = await CreateClient().ValidateAsync(root);

            Assert.True(result.CanActivate, string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.False(File.Exists(Path.Combine(root, "setting.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task OneShotValidatorKeepsRealParserErrorsBlocking()
    {
        string root = Directory.CreateTempSubdirectory("cloudemuera-real-validator-error-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "CSV"));
            Directory.CreateDirectory(Path.Combine(root, "ERB"));
            await File.WriteAllTextAsync(Path.Combine(root, "CSV", "GAMEBASE.CSV"), "コード,错误测试\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "ERB", "START.ERB"),
                "@SYSTEM_TITLE\n+NOT_INCREMENT\nQUIT\n");
            await File.WriteAllTextAsync(Path.Combine(root, "emuera.config"), "Use sav folder:NO\n");

            GameParserValidationResult result = await CreateClient().ValidateAsync(root);

            Assert.False(result.CanActivate);
            Assert.Contains(result.Diagnostics, item =>
                item.Code == "RUNTIME_INITIALIZATION_FAILED" &&
                item.Severity == "ERROR" &&
                item.ActivationBlocking);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static GameValidatorProcessClient CreateClient() => new(new GameValidatorProcessOptions
    {
        ExecutablePath = "dotnet",
        AssemblyPath = Path.Combine(FindRepositoryRoot(), "src", "CloudEmuera.Validator", "bin", "Release", "net10.0", "CloudEmuera.Validator.dll"),
        Timeout = TimeSpan.FromSeconds(20),
    });

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CloudEmuera.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("CloudEmuera.slnx was not found.");
    }
}
