namespace CloudEmuera.Application.Fonts;

/// <summary>
/// The only font face that a new Session may use when no explicit choice was
/// supplied by an older client or database row.
/// </summary>
public static class RuntimeFontDefaults
{
    public const string DefaultFaceId = "sarasa-fixed-sc-1.0.40-regular";
}

public sealed record RuntimeFontFace(
    string FaceId,
    string DisplayName,
    string Family,
    string SourceVersion,
    int Weight,
    string RuntimeFamilyName,
    string RuntimeTtfPath,
    string RuntimeTtfSha256,
    long RuntimeTtfByteLength,
    string WebWoff2Path,
    string WebWoff2Sha256,
    long WebWoff2ByteLength,
    string LicenseId,
    string LicenseFile);

/// <summary>
/// Read-only catalog boundary. Implementations own all path and digest
/// validation; callers only persist and transmit the exact face ID.
/// </summary>
public interface IRuntimeFontCatalog
{
    string CatalogDigest { get; }

    IReadOnlyList<RuntimeFontFace> ListAvailable();

    RuntimeFontFace Require(string faceId);
}

public sealed class RuntimeFontCatalogException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
