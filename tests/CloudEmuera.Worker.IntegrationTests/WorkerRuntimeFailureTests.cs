using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

public sealed class WorkerRuntimeFailureTests
{
    [Fact]
    public void FailureSelectionPrefersCurrentFatalDiagnosticOverHistoricalWarnings()
    {
        var warning = new EmueraRuntimeDiagnostic(
            "runtime_warning",
            EmueraRuntimePhase.Initialization,
            "historical warning",
            IsFatal: false);
        var fatal = new EmueraRuntimeDiagnostic(
            "runtime_script_failed",
            EmueraRuntimePhase.Execution,
            "current execution failure",
            IsFatal: true);
        var result = new EmueraRuntimeResult(
            EmueraRuntimeStatus.ScriptFailed,
            [warning, fatal]);

        EmueraRuntimeDiagnostic selected = WorkerRuntimeController.SelectFailureDiagnostic(result, "execution");

        Assert.Same(fatal, selected);
        Assert.True(selected.IsFatal);
    }

    [Fact]
    public void FailureSelectionDoesNotUpgradeNonfatalDiagnostic()
    {
        var warning = new EmueraRuntimeDiagnostic(
            "runtime_warning",
            EmueraRuntimePhase.Initialization,
            "nonfatal warning",
            IsFatal: false);
        var result = new EmueraRuntimeResult(
            EmueraRuntimeStatus.ScriptFailed,
            [warning]);

        EmueraRuntimeDiagnostic selected = WorkerRuntimeController.SelectFailureDiagnostic(result, "execution");

        Assert.Same(warning, selected);
        Assert.False(selected.IsFatal);
    }
}
