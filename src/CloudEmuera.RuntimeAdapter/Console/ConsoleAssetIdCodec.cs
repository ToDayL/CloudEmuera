using System.Buffers;
using System.Text;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Encodes the logical path of a presentation asset without making the path
/// itself a request path. New ids are reversible path aliases; the old
/// sha256-* aliases remain readable for sessions created before P1-S07.
/// </summary>
public static class ConsoleAssetIdCodec
{
    public const string PathPrefix = "path-";
    public const string LegacySha256Prefix = "sha256-";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string EncodePath(string logicalPath)
    {
        RuntimeRelativePath path = RuntimeRelativePath.Parse(logicalPath.Normalize(NormalizationForm.FormC));
        byte[] bytes = StrictUtf8.GetBytes(path.Value);
        string token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string value = PathPrefix + token;
        _ = new ConsoleAssetId(value);
        return value;
    }

    public static bool TryDecodePath(string value, out string logicalPath)
    {
        logicalPath = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > ConsoleContractLimits.Default.MaxAssetIdLength || !value.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string token = value[PathPrefix.Length..];
        if (token.Length == 0 || token.Any(static character =>
                character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not ('-' or '_')))
        {
            return false;
        }

        int padding = (4 - token.Length % 4) % 4;
        string base64 = token.Replace('-', '+').Replace('_', '/') + new string('=', padding);
        int byteCount = checked((token.Length * 3) / 4 + (padding == 0 ? 0 : 1));
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(byteCount, 1));
        try
        {
            if (!Convert.TryFromBase64String(base64, bytes, out int written))
            {
                return false;
            }

            string decoded = StrictUtf8.GetString(bytes, 0, written).Normalize(NormalizationForm.FormC);
            if (!RuntimeRelativePath.TryParse(decoded, out RuntimeRelativePath path) ||
                !string.Equals(EncodePath(path.Value), value, StringComparison.Ordinal))
            {
                return false;
            }

            logicalPath = path.Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static bool TryGetLegacyDigest(string value, out string digest)
    {
        digest = string.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(LegacySha256Prefix, StringComparison.Ordinal) || value.Length != LegacySha256Prefix.Length + 64)
        {
            return false;
        }

        string hex = value[LegacySha256Prefix.Length..];
        if (!hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        digest = $"sha256:{hex.ToLowerInvariant()}";
        return true;
    }

    public static bool IsLegacyDigestId(string value) => TryGetLegacyDigest(value, out _);
}
