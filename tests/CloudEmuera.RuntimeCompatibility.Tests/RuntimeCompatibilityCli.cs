using System.Text.Json;

namespace CloudEmuera.RuntimeCompatibility.Tests;

internal static class RuntimeCompatibilityCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? scenario = null;
        string? fixture = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--scenario" when index + 1 < args.Length:
                    scenario = args[++index];
                    break;
                case "--fixture" when index + 1 < args.Length:
                    fixture = args[++index];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    return 2;
            }
        }

        if (scenario != "input-roundtrip")
        {
            Console.Error.WriteLine("Unsupported scenario. Supported value: input-roundtrip");
            return 2;
        }

        if (fixture is not null && !RuntimeScenarioRunner.FixtureIds.Contains(fixture, StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Unknown fixture. Supported values: v18-core, em-ee-core");
            return 2;
        }

        string repositoryRoot = FindRepositoryRoot();
        IReadOnlyList<string> fixtures = fixture is null ? RuntimeScenarioRunner.FixtureIds : [fixture];
        var reports = new List<RuntimeScenarioReport>();
        foreach (string fixtureId in fixtures)
        {
            RuntimeScenarioReport report = await RuntimeScenarioRunner.RunAsync(repositoryRoot, fixtureId).ConfigureAwait(false);
            reports.Add(report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        int totalAssertions = reports.Sum(report => report.AssertionCount);
        bool passed = reports.All(report => report.Status == "Completed");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario,
            status = passed ? "passed" : "failed",
            fixtureCount = reports.Count,
            assertionCount = totalAssertions
        }));
        return passed ? 0 : 1;
    }

    internal static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? current = new(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "tests", "fixtures", "runtime", "manifest.json")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException("Unable to locate tests/fixtures/runtime/manifest.json.");
    }
}
