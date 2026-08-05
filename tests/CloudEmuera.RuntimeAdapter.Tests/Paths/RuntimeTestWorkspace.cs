using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

internal sealed class RuntimeTestWorkspace : IDisposable
{
    public RuntimeTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-tests", Guid.NewGuid().ToString("N"));
        GameVersionRoot = Path.Combine(Root, "game-version");
        SessionWorkspaceRoot = Path.Combine(Root, "session-workspace");
        Directory.CreateDirectory(Path.Combine(GameVersionRoot, "CSV"));
        Directory.CreateDirectory(Path.Combine(GameVersionRoot, "ERB"));
        Directory.CreateDirectory(Path.Combine(GameVersionRoot, "resources"));
        File.WriteAllText(Path.Combine(GameVersionRoot, "CSV", "GAMEBASE.CSV"), "; test\n");
        File.WriteAllText(Path.Combine(GameVersionRoot, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        File.WriteAllText(Path.Combine(GameVersionRoot, "emuera.config"), "Use sav folder:NO\n");
    }

    public string Root { get; }

    public string GameVersionRoot { get; }

    public string SessionWorkspaceRoot { get; }

    public RuntimePaths BuildPaths(RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
    {
        if (saveLayout == RuntimeSaveLayout.SavDirectory)
        {
            File.WriteAllText(Path.Combine(GameVersionRoot, "emuera.config"), "Use sav folder:YES\n");
        }

        SessionRootLayout layout = new SessionRootLayoutBuilder(
            GameVersionRoot,
            SessionWorkspaceRoot,
            saveLayout).Build();
        return layout.RuntimePaths;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
