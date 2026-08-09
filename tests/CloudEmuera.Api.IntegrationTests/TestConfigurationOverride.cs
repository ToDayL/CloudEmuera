[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace CloudEmuera.Api.IntegrationTests;

internal sealed class TestConfigurationOverride : IDisposable
{
    private readonly Dictionary<string, string?> _previous = [];

    public TestConfigurationOverride(string dataRoot, bool includeBootstrap = false)
    {
        Set("CloudEmuera__DataPath", dataRoot);
        Set("CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME", includeBootstrap ? "identity-admin" : null);
        Set("CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL", includeBootstrap ? "admin@example.test" : null);
        Set("CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD", includeBootstrap ? "temporary-password" : null);
    }

    public void Dispose()
    {
        foreach ((string key, string? value) in _previous) Environment.SetEnvironmentVariable(key, value);
    }

    private void Set(string key, string? value)
    {
        _previous[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }
}
