using System.Text;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Reads only the fixed upstream <c>Use sav folder</c> setting (the
/// <c>Config.UseSaveFolder</c> property), including the upstream Japanese
/// boolean spellings <c>前</c> and <c>後</c>. This is not a
/// replacement for Emuera's complete configuration parser; it is the narrow
/// host-side check used to bind a SessionRoot to one native save layout.
/// </summary>
public static class EmueraSaveLayoutInspector
{
    private const string UpstreamEnglishUseSaveFolderKey = "Use sav folder";
    private const string UpstreamJapaneseUseSaveFolderKey = "セーブデータをsavフォルダ内に作成する";
    private const string SimplifiedChineseUseSaveFolderKey = "在sav文件夹中创建存档";
    private const int MaximumConfigurationBytes = 1024 * 1024;

    public static RuntimeSaveLayout Inspect(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        bool? configured = null;
        foreach (string rawLine in configurationText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (!IsUseSaveFolderKey(key))
            {
                continue;
            }

            bool value = ParseBoolean(line[(separator + 1)..].Trim());
            if (configured is bool previous && previous != value)
            {
                throw new RuntimeSaveLayoutInspectionException(
                    "UseSaveFolder is declared more than once with conflicting values.");
            }

            configured = value;
        }

        return configured is true ? RuntimeSaveLayout.SavDirectory : RuntimeSaveLayout.Root;
    }

    public static RuntimeSaveLayout Inspect(ReadOnlySpan<byte> configurationBytes)
    {
        if (configurationBytes.Length > MaximumConfigurationBytes)
        {
            throw new RuntimeSaveLayoutInspectionException(
                "The runtime configuration exceeds the supported inspection size.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        ReadOnlySpan<byte> payload = configurationBytes;
        if (payload.StartsWith("\uFEFF"u8))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            payload = payload[3..];
        }
        else
        {
            Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return Inspect(utf8.GetString(payload));
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
        }

        try
        {
            return Inspect(encoding.GetString(payload));
        }
        catch (DecoderFallbackException exception)
        {
            throw new RuntimeSaveLayoutInspectionException(
                "The runtime configuration encoding is not valid UTF-8 or Shift-JIS.",
                exception);
        }
    }

    public static RuntimeSaveLayout Inspect(Stream configurationStream)
    {
        ArgumentNullException.ThrowIfNull(configurationStream);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = configurationStream.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (buffer.Length + read > MaximumConfigurationBytes)
            {
                throw new RuntimeSaveLayoutInspectionException(
                    "The runtime configuration exceeds the supported inspection size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Inspect(buffer.ToArray());
    }

    public static RuntimeSaveLayout InspectFile(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        using FileStream stream = new(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Inspect(stream);
    }

    private static bool ParseBoolean(string value)
    {
        if (value.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("前", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Equals("NO", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("後", StringComparison.Ordinal))
        {
            return false;
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long numeric))
        {
            return numeric != 0;
        }

        throw new RuntimeSaveLayoutInspectionException(
            "UseSaveFolder has an invalid boolean value.");
    }

    private static bool IsUseSaveFolderKey(string key) =>
        key.Equals(UpstreamEnglishUseSaveFolderKey, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(UpstreamJapaneseUseSaveFolderKey, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(SimplifiedChineseUseSaveFolderKey, StringComparison.OrdinalIgnoreCase);
}

public sealed class RuntimeSaveLayoutInspectionException : RuntimePathException
{
    public RuntimeSaveLayoutInspectionException(string message, Exception? innerException = null)
        : base(
            RuntimePathReasonCodes.LayoutConflict,
            message,
            "emuera.config",
            RuntimeFileArea.Configuration,
            innerException)
    {
    }
}
