using System;
using System.IO;
using System.Text;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

/// <summary>
/// Applies the pinned upstream desktop configuration bootstrap without
/// starting the game parser.  The desktop entry point calls
/// <c>LoadConfig()</c>, which loads the default, root and fixed configuration
/// files and writes the root configuration when it is absent or stale.  Game
/// ingestion uses the same behavior so the published Game tree contains the
/// exact configuration the headless runtime will later consume.
/// </summary>
public static class RuntimeConfigMaterializer
{
    public static RuntimeConfigMaterializationResult Materialize(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"The runtime configuration root does not exist: '{fullRoot}'.");

        HeadlessPathResolver.Configure(fullRoot);
        string configPath = Path.Combine(fullRoot, "emuera.config");
        try
        {
            bool existedBefore = HeadlessFile.Exists(configPath);
            byte[] before = existedBefore ? HeadlessFile.ReadAllBytes(configPath) : null;

            MinorShift.Emuera.Program.ConfigureHeadless(
                fullRoot,
                Path.Combine(fullRoot, "CSV"),
                Path.Combine(fullRoot, "ERB"),
                Path.Combine(fullRoot, "tmp"),
                Path.Combine(fullRoot, "resources"),
                Path.Combine(fullRoot, "sound"),
                Path.Combine(fullRoot, "font"));
            ConfigData.ResetHeadless();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (!ConfigData.Instance.LoadConfig(applyConfig: true))
                throw new InvalidDataException("The pinned upstream configuration loader rejected the game content.");
            if (!HeadlessFile.Exists(configPath))
                throw new InvalidDataException("The pinned upstream configuration loader did not create emuera.config.");

            byte[] after = HeadlessFile.ReadAllBytes(configPath);
            bool changed = before is null || !before.AsSpan().SequenceEqual(after);
            return new RuntimeConfigMaterializationResult(!existedBefore, changed);
        }
        finally
        {
            HeadlessPathResolver.Reset();
        }
    }
}

public sealed record RuntimeConfigMaterializationResult(bool Created, bool Changed);
