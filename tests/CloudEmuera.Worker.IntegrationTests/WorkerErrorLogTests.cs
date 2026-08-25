using System.Text.Json;
using CloudEmuera.Ipc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

public sealed class WorkerErrorLogTests
{
    [Fact]
    [Trait("Category", "WorkerDiagnostics")]
    public void WritesStructuredRecordToSessionMetadataAndRedactsBootstrapSecrets()
    {
        string container = CreateContainer(out string sessionRoot);
        try
        {
            WorkerBootstrapDocument bootstrap = CreateBootstrap(sessionRoot);
            WorkerErrorLog.Append(
                bootstrap,
                bootstrap.Binding,
                "runtime_failed",
                "runtime_worker_failure",
                "execution",
                $"failed at {sessionRoot}; socket={bootstrap.ControlSocketPath}; token={bootstrap.BootstrapToken}",
                LogLevel.Error,
                fatal: true,
                lastOutputSequence: 7,
                exceptionType: "InvalidOperationException");

            string path = ErrorLogPath(sessionRoot);
            Assert.True(File.Exists(path));
            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement record = document.RootElement;
            Assert.Equal(1, record.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("runtime_failed", record.GetProperty("eventName").GetString());
            Assert.Equal("sess_error_log", record.GetProperty("sessionId").GetString());
            Assert.Equal("wrk_error_log", record.GetProperty("workerId").GetString());
            Assert.Equal(3UL, record.GetProperty("workerEpoch").GetUInt64());
            Assert.Equal("runtime_worker_failure", record.GetProperty("code").GetString());
            Assert.Equal("execution", record.GetProperty("phase").GetString());
            Assert.True(record.GetProperty("fatal").GetBoolean());
            Assert.Equal(7, record.GetProperty("lastOutputSequence").GetInt64());
            Assert.Equal("InvalidOperationException", record.GetProperty("exceptionType").GetString());
            Assert.DoesNotContain(sessionRoot, line, StringComparison.Ordinal);
            Assert.DoesNotContain(bootstrap.ControlSocketPath, line, StringComparison.Ordinal);
            Assert.DoesNotContain(bootstrap.BootstrapToken, line, StringComparison.Ordinal);
            Assert.Contains("<redacted>", record.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WorkerDiagnostics")]
    public void TruncatesExistingLogBeforeItExceedsBound()
    {
        string container = CreateContainer(out string sessionRoot);
        try
        {
            string metadataDirectory = Path.Combine(container, "metadata");
            Directory.CreateDirectory(metadataDirectory);
            string path = ErrorLogPath(sessionRoot);
            File.WriteAllBytes(path, new byte[WorkerErrorLog.MaxFileBytes]);

            WorkerBootstrapDocument bootstrap = CreateBootstrap(sessionRoot);
            WorkerErrorLog.Append(
                bootstrap,
                bootstrap.Binding,
                "runtime_failed",
                "current_failure",
                "execution",
                "current failure survives truncation",
                LogLevel.Error,
                fatal: true);

            Assert.InRange(new FileInfo(path).Length, 1, WorkerErrorLog.MaxFileBytes);
            string[] lines = File.ReadAllLines(path);
            Assert.Contains(lines, line => line.Contains("current_failure", StringComparison.Ordinal));
            foreach (string line in lines)
                using (JsonDocument.Parse(line)) { }
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    private static WorkerBootstrapDocument CreateBootstrap(string sessionRoot) => new()
    {
        SessionId = "sess_error_log",
        WorkerId = "wrk_error_log",
        WorkerEpoch = 3,
        SessionRoot = sessionRoot,
        ControlSocketPath = Path.Combine(Path.GetDirectoryName(sessionRoot)!, "worker.sock"),
        BootstrapToken = "bootstrap-secret-for-test"
    };

    private static string CreateContainer(out string sessionRoot)
    {
        string container = Path.Combine(Path.GetTempPath(), "cloudemuera-worker-error", Guid.NewGuid().ToString("N"));
        sessionRoot = Path.Combine(container, "root");
        Directory.CreateDirectory(sessionRoot);
        return container;
    }

    private static string ErrorLogPath(string sessionRoot) =>
        Path.Combine(Path.GetDirectoryName(sessionRoot)!, "metadata", WorkerErrorLog.FileName);
}
