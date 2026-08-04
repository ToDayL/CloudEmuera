namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

public static class RuntimeFixtureRepository
{
    public static string FindRepositoryRoot()
    {
        var starts = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (string start in starts.Distinct(StringComparer.Ordinal))
        {
            DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                string manifestPath = Path.Combine(current.FullName, "tests", "fixtures", "runtime", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "Unable to locate the repository root. Expected tests/fixtures/runtime/manifest.json above the test output or current directory.");
    }

    public static string FindFixtureRoot()
    {
        return Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "runtime");
    }
}
