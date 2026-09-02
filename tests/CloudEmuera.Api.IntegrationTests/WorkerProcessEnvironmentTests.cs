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
        foreach (string variable in WorkerProcessEnvironment.ControlPlaneSecretVariableNames)
            startInfo.Environment[variable] = "must-not-reach-worker";
        startInfo.Environment["CloudEmuera__Capacity__MaxActiveWorkers"] = "8";
        startInfo.Environment["CLOUDEMUERA_RUNTIME_DEBUG_TRACE"] = "1";
        startInfo.Environment["CLOUDEMUERA_WORKER_TEST_VALUE"] = "preserved";

        WorkerProcessEnvironment.RemoveHostOrchestratorVariables(startInfo);

        foreach (string variable in WorkerProcessEnvironment.HostOrchestratorVariableNames)
            Assert.False(startInfo.Environment.ContainsKey(variable), variable);
        foreach (string variable in WorkerProcessEnvironment.ControlPlaneSecretVariableNames)
            Assert.False(startInfo.Environment.ContainsKey(variable), variable);
        Assert.False(startInfo.Environment.ContainsKey("CloudEmuera__Capacity__MaxActiveWorkers"));
        Assert.Equal("1", startInfo.Environment["CLOUDEMUERA_RUNTIME_DEBUG_TRACE"]);
        Assert.Equal("preserved", startInfo.Environment["CLOUDEMUERA_WORKER_TEST_VALUE"]);
    }
}
