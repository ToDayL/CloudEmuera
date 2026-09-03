// CloudEmuera modification: inject the explicit per-Worker random seed used
// by trace capture and replay before the pinned evaluator creates either RNG.
using System.Threading;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

public static class HeadlessDeterminism
{
    private static long randomSeed;

    public static long RandomSeed => Interlocked.Read(ref randomSeed);

    public static void ConfigureRandomSeed(long value) => Interlocked.Exchange(ref randomSeed, value);

    public static int RandomSeed32 => unchecked((int)RandomSeed);
}
