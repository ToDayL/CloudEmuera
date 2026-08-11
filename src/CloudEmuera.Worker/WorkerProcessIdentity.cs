using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudEmuera.Worker;

internal static class WorkerProcessIdentityProbe
{
    public static string ReadBootId()
    {
        if (!OperatingSystem.IsLinux())
            return "non-linux";

        string value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (value.Length != 36)
            throw new InvalidDataException("The Linux boot ID is invalid.");
        return value;
    }

    public static long ReadStartTicks(long processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.Ticks;
        }

        string stat = File.ReadAllText($"/proc/{processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/stat");
        int closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis < 0 || closingParenthesis + 2 >= stat.Length)
            throw new InvalidDataException("The Linux process stat record is invalid.");
        string[] fields = stat[(closingParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The text after the command name starts at field 3, therefore field
        // 22 (starttime) is index 19 in this suffix.
        if (fields.Length <= 19 || !long.TryParse(fields[19], out long startTicks) || startTicks <= 0)
            throw new InvalidDataException("The Linux process start ticks are invalid.");
        return startTicks;
    }
}

internal static class ParentDeathGuard
{
    private const int PrSetPdeathsig = 1;
    private const int SigKill = 9;

    public static void Install(long expectedParentProcessId)
    {
        if (expectedParentProcessId <= 0)
            throw new InvalidDataException("The expected parent process ID is invalid.");

        if (!OperatingSystem.IsLinux())
            return;

        if (Prctl(PrSetPdeathsig, SigKill, 0, 0, 0) != 0)
            throw new InvalidOperationException("The Worker parent-death signal could not be installed.");

        if (GetParentProcessId() != expectedParentProcessId)
            throw new InvalidOperationException("The Worker parent process changed before the death guard was installed.");
    }

    [SuppressMessage("Security", "CA2101", Justification = "The libc entry points use fixed numeric arguments.")]
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern long GetParentProcessId();
}
