using System.Diagnostics;
using System.Text.Json;
using CloudEmuera.Application.Games;

namespace CloudEmuera.Infrastructure.Games;

public sealed record GameValidatorProcessOptions
{
    public required string ExecutablePath { get; init; }
    public string? AssemblyPath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxOutputBytes { get; init; } = 64 * 1024;
}

public sealed class GameValidatorProcessClient(GameValidatorProcessOptions options) : IGameContentValidator
{
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);

    public async Task<GameContentPreparationResult> PrepareAsync(string snapshotRoot, CancellationToken cancellationToken = default)
    {
        GameParserValidationResult result = await ExecuteAsync(snapshotRoot, "--prepare-config", cancellationToken).ConfigureAwait(false);
        return new(result.CanActivate, result.Diagnostics);
    }

    public Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default) =>
        ExecuteAsync(snapshotRoot, command: null, cancellationToken);

    private async Task<GameParserValidationResult> ExecuteAsync(
        string snapshotRoot,
        string? command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        var start = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (options.AssemblyPath is not null) start.ArgumentList.Add(options.AssemblyPath);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(snapshotRoot);
        if (command is not null) start.ArgumentList.Add(command);

        using var process = new Process { StartInfo = start };
        string failurePrefix = command is null ? "VALIDATOR" : "CONFIG_GENERATOR";
        try
        {
            if (!process.Start()) return Failure($"{failurePrefix}_START_FAILED", failurePrefix);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failure($"{failurePrefix}_START_FAILED", failurePrefix);
        }

        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaxOutputBytes, cancellationToken);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, options.MaxOutputBytes, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
            byte[] payload = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) return Failure($"{failurePrefix}_CRASHED", failurePrefix);
            return Parse(payload, failurePrefix);
        }
        catch (TimeoutException)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            return Failure($"{failurePrefix}_TIMEOUT", failurePrefix);
        }
        catch (ValidatorOutputLimitException)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            return Failure($"{failurePrefix}_OUTPUT_LIMIT", failurePrefix);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            return Failure($"{failurePrefix}_PROTOCOL_ERROR", failurePrefix);
        }
    }

    private static GameParserValidationResult Parse(byte[] payload, string operation)
    {
        ValidatorResponse? response = JsonSerializer.Deserialize<ValidatorResponse>(payload, ProtocolJson);
        if (response is null || response.SchemaVersion != 1 || response.Diagnostics is null || response.Diagnostics.Count > 256)
            return Failure($"{operation}_PROTOCOL_ERROR", operation);
        if (response.Diagnostics.Any(item => string.IsNullOrWhiteSpace(item.Code) || item.Code.Length > 100 || item.Message.Length > 500))
            return Failure($"{operation}_PROTOCOL_ERROR", operation);
        IReadOnlyList<GameValidationDiagnostic> diagnostics = response.Diagnostics.Select(item =>
            // The blocking flag is the protocol's authoritative severity bit.
            // Normalize the display severity at this boundary so a malformed or
            // stale Validator cannot persist an ERROR that does not block, or a
            // blocking diagnostic that is labelled as a warning.
            new GameValidationDiagnostic(
                item.Code,
                item.ActivationBlocking ? "ERROR" : "WARNING",
                item.Path,
                item.Message,
                item.ActivationBlocking)).ToArray();
        bool canActivate = response.CanActivate && !diagnostics.Any(item => item.ActivationBlocking);
        return new(canActivate, diagnostics);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        if (maximum is < 1024 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var buffer = new MemoryStream(Math.Min(maximum, 16 * 1024));
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximum) throw new ValidatorOutputLimitException();
            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task ObserveAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ValidatorOutputLimitException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static GameParserValidationResult Failure(string code, string operation) => new(false,
        [new GameValidationDiagnostic(code, "ERROR", null, operation == "VALIDATOR"
            ? "The parser-only validator did not return a valid result."
            : "The pinned runtime configuration normalizer did not return a valid result.", true)]);

    private sealed record ValidatorResponse(int SchemaVersion, bool CanActivate, IReadOnlyList<ValidatorDiagnostic>? Diagnostics);
    private sealed record ValidatorDiagnostic(string Code, string Severity, string? Path, string Message, bool ActivationBlocking);
    private sealed class ValidatorOutputLimitException : IOException;
}
