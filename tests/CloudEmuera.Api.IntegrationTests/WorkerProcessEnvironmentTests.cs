using System.Diagnostics;
using CloudEmuera.Api.Workers;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class WorkerProcessEnvironmentTests
{
    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public void WorkerLaunchDoesNotInheritDotnetWatchOrHotReloadControl()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        foreach (string variable in WorkerProcessEnvironment.HostOrchestratorVariableNames)
            startInfo.Environment[variable] = "inherited-from-api";
        startInfo.Environment["CLOUDEMUERA_WORKER_TEST_VALUE"] = "preserved";

        WorkerProcessEnvironment.RemoveHostOrchestratorVariables(startInfo);

        foreach (string variable in WorkerProcessEnvironment.HostOrchestratorVariableNames)
            Assert.False(startInfo.Environment.ContainsKey(variable), variable);
        Assert.Equal("preserved", startInfo.Environment["CLOUDEMUERA_WORKER_TEST_VALUE"]);
    }
}
