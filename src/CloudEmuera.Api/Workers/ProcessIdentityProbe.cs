using System.Diagnostics;

namespace CloudEmuera.Api.Workers;

internal static class ProcessIdentityProbe
{
    public static CloudEmuera.Application.Sessions.Runtime.WorkerProcessIdentity Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new CloudEmuera.Application.Sessions.Runtime.WorkerProcessIdentity(
            process.Id,
            ReadBootId(),
            ReadStartTicks(process.Id));
    }

    public static string ReadBootId()
    {
        if (!OperatingSystem.IsLinux())
            return "non-linux";
        string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        return bootId.Length == 36 ? bootId : throw new InvalidDataException("The Linux boot ID is invalid.");
    }

    public static long ReadStartTicks(long processId)
    {
        if (!OperatingSystem.IsLinux())
            return Process.GetProcessById((int)processId).StartTime.Ticks;
        string text = File.ReadAllText($"/proc/{processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/stat");
        int closingParenthesis = text.LastIndexOf(')');
        if (closingParenthesis < 0)
            throw new InvalidDataException("The process stat record is invalid.");
        string[] fields = text[(closingParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !long.TryParse(fields[19], out long startTicks) || startTicks <= 0)
            throw new InvalidDataException("The process start ticks are invalid.");
        return startTicks;
    }

    public static bool IsSame(CloudEmuera.Application.Sessions.Runtime.WorkerProcessIdentity expected)
    {
        try
        {
            return ReadStartTicks(expected.ProcessId) == expected.ProcessStartTicks &&
                string.Equals(ReadBootId(), expected.ProcessBootId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<bool> TerminateExactAsync(
        CloudEmuera.Application.Sessions.Runtime.WorkerProcessIdentity expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (expected.ProcessId == Environment.ProcessId)
            return false;
        Process process;
        try
        {
            process = Process.GetProcessById(checked((int)expected.ProcessId));
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            CloudEmuera.Application.Sessions.Runtime.WorkerProcessIdentity actual = Read(process);
            if (actual != expected)
                return true;
            if (process.HasExited)
                return true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return process.HasExited;
        }
    }
}
