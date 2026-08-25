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

    public async Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default)
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

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Failure("VALIDATOR_START_FAILED");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failure("VALIDATOR_START_FAILED");
        }

        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaxOutputBytes, cancellationToken);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, options.MaxOutputBytes, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
            byte[] payload = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) return Failure("VALIDATOR_CRASHED");
            return Parse(payload);
        }
        catch (TimeoutException)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            return Failure("VALIDATOR_TIMEOUT");
        }
        catch (ValidatorOutputLimitException)
        {
            Kill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            return Failure("VALIDATOR_OUTPUT_LIMIT");
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
            return Failure("VALIDATOR_PROTOCOL_ERROR");
        }
    }

    private static GameParserValidationResult Parse(byte[] payload)
    {
        ValidatorResponse? response = JsonSerializer.Deserialize<ValidatorResponse>(payload, ProtocolJson);
        if (response is null || response.SchemaVersion != 1 || response.Diagnostics is null || response.Diagnostics.Count > 256)
            return Failure("VALIDATOR_PROTOCOL_ERROR");
        if (response.Diagnostics.Any(item => string.IsNullOrWhiteSpace(item.Code) || item.Code.Length > 100 || item.Message.Length > 500))
            return Failure("VALIDATOR_PROTOCOL_ERROR");
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

    private static GameParserValidationResult Failure(string code) => new(false,
        [new GameValidationDiagnostic(code, "ERROR", null, "The parser-only validator did not return a valid result.", true)]);

    private sealed record ValidatorResponse(int SchemaVersion, bool CanActivate, IReadOnlyList<ValidatorDiagnostic>? Diagnostics);
    private sealed record ValidatorDiagnostic(string Code, string Severity, string? Path, string Message, bool ActivationBlocking);
    private sealed class ValidatorOutputLimitException : IOException;
}
