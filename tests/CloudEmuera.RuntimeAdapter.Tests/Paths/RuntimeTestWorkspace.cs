using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

internal sealed class RuntimeTestWorkspace : IDisposable
{
    public RuntimeTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-tests", Guid.NewGuid().ToString("N"));
        GameContentRoot = Path.Combine(Root, "game-content");
        SessionWorkspaceRoot = Path.Combine(Root, "session-workspace");
        Directory.CreateDirectory(Path.Combine(GameContentRoot, "CSV"));
        Directory.CreateDirectory(Path.Combine(GameContentRoot, "ERB"));
        Directory.CreateDirectory(Path.Combine(GameContentRoot, "resources"));
        File.WriteAllText(Path.Combine(GameContentRoot, "CSV", "GAMEBASE.CSV"), "; test\n");
        File.WriteAllText(Path.Combine(GameContentRoot, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        File.WriteAllText(Path.Combine(GameContentRoot, "emuera.config"), "Use sav folder:NO\n");
    }

    public string Root { get; }

    public string GameContentRoot { get; }

    public string SessionWorkspaceRoot { get; }

    public RuntimePaths BuildPaths(RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
    {
        if (saveLayout == RuntimeSaveLayout.SavDirectory)
        {
            File.WriteAllText(Path.Combine(GameContentRoot, "emuera.config"), "Use sav folder:YES\n");
        }

        SessionRootLayout layout = new SessionRootLayoutBuilder(
            GameContentRoot,
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
