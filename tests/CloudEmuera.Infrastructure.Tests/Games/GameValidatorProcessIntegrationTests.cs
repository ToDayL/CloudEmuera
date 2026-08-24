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
