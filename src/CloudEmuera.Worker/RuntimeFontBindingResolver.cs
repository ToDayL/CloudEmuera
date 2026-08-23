using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudEmuera.Worker;

internal sealed record RuntimeFontBinding(
    string FaceId,
    string RuntimeFamilyName,
    string RuntimeTtfPath,
    string CatalogDigest,
    string WebWoff2Sha256);

internal sealed class RuntimeFontBindingException : Exception
{
    public RuntimeFontBindingException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    public string SafeMessage => Message;
}

/// <summary>
/// Resolves the exact immutable font selected by the API. The Worker never
/// accepts a font path from a Session or game package; it derives the path
/// from its own image-owned catalog and verifies the bytes before loading.
/// </summary>
internal static class RuntimeFontBindingResolver
{
    private const string RootEnvironmentVariable = "CLOUDEMUERA_RUNTIME_FONT_ROOT";
    private static readonly IReadOnlyDictionary<string, string> ExpectedRuntimeFamilies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sarasa-fixed-sc-1.0.40-light"] = "Sarasa Fixed SC",
            ["sarasa-fixed-sc-1.0.40-regular"] = "Sarasa Fixed SC",
            ["sarasa-fixed-sc-1.0.40-medium"] = "Sarasa Fixed SC",
            ["lxgw-bright-code-2.922-extralight"] = "LXGW Bright Code",
            ["lxgw-bright-code-2.922-light"] = "LXGW Bright Code",
            ["lxgw-bright-code-2.922-regular"] = "LXGW Bright Code",
        };

    public static RuntimeFontBinding Resolve(string faceId, string expectedCatalogDigest)
    {
        if (string.IsNullOrWhiteSpace(faceId) || faceId.Length > 128 ||
            faceId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~')))
            throw Unavailable("The selected runtime font face is unavailable.");

        if (!string.IsNullOrEmpty(expectedCatalogDigest) && !IsLowerSha256(expectedCatalogDigest))
            throw CatalogMismatch("The runtime font catalog digest is invalid.");

        string root = Environment.GetEnvironmentVariable(RootEnvironmentVariable)
            ?? Path.Combine(AppContext.BaseDirectory, "runtime-fonts");
        root = Path.GetFullPath(root);
        EnsureDirectory(root);
        string catalogPath = Path.Combine(root, "catalog.json");
        EnsureRegularFile(catalogPath, "font_catalog_mismatch");
        byte[] catalogBytes = File.ReadAllBytes(catalogPath);
        string catalogDigest = Sha256(catalogBytes);
        if (!string.IsNullOrEmpty(expectedCatalogDigest) && !string.Equals(catalogDigest, expectedCatalogDigest, StringComparison.Ordinal))
            throw CatalogMismatch("The runtime font catalog digest does not match the Worker image.");

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(catalogBytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw CatalogMismatch("The runtime font catalog is invalid.", exception);
        }

        if (document is null || document.SchemaVersion != 1 ||
            !string.Equals(document.DefaultFaceId, "sarasa-fixed-sc-1.0.40-regular", StringComparison.Ordinal) ||
            document.Items is null || document.Items.Count != ExpectedRuntimeFamilies.Count ||
            document.Items.Any(item => item is null) ||
            !ExpectedRuntimeFamilies.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(document.Items.Select(item => item.FaceId)))
            throw CatalogMismatch("The runtime font catalog schema is unsupported.");

        CatalogItem? item = document.Items.SingleOrDefault(candidate => string.Equals(candidate.FaceId, faceId, StringComparison.Ordinal));
        if (item is null || string.IsNullOrWhiteSpace(item.RuntimeFamilyName) || string.IsNullOrWhiteSpace(item.RuntimeTtfPath) ||
            !ExpectedRuntimeFamilies.TryGetValue(item.FaceId, out string? expectedFamily) || !string.Equals(item.RuntimeFamilyName, expectedFamily, StringComparison.Ordinal) ||
            !IsLowerSha256(item.RuntimeTtfSha256) || item.RuntimeTtfByteLength <= 0 || !IsLowerSha256(item.WebWoff2Sha256) || item.WebWoff2ByteLength <= 0)
            throw Unavailable("The selected runtime font face is unavailable.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogItem candidate in document.Items)
        {
            if (!paths.Add(NormalizeDeclaredPath(candidate.RuntimeTtfPath)) || !paths.Add(NormalizeDeclaredPath(candidate.WebWoff2Path)))
                throw CatalogMismatch("The runtime font catalog reuses an asset path.");
        }

        string ttfPath = ResolveDeclaredPath(root, item.RuntimeTtfPath);
        EnsureRegularFile(ttfPath, "runtime_font_unavailable");
        FileInfo info = new(ttfPath);
        if (info.Length != item.RuntimeTtfByteLength || !string.Equals(Sha256(File.ReadAllBytes(ttfPath)), item.RuntimeTtfSha256, StringComparison.Ordinal))
            throw Unavailable("The selected runtime font bytes do not match the catalog.");

        string webWoff2Path = ResolveDeclaredPath(root, item.WebWoff2Path);
        EnsureRegularFile(webWoff2Path, "runtime_font_unavailable");
        FileInfo webInfo = new(webWoff2Path);
        if (webInfo.Length != item.WebWoff2ByteLength || !string.Equals(Sha256(File.ReadAllBytes(webWoff2Path)), item.WebWoff2Sha256, StringComparison.Ordinal))
            throw Unavailable("The selected runtime web font bytes do not match the catalog.");

        return new RuntimeFontBinding(faceId, item.RuntimeFamilyName, ttfPath, catalogDigest, item.WebWoff2Sha256);
    }

    private static string ResolveDeclaredPath(string root, string relativePath)
    {
        relativePath = NormalizeDeclaredPath(relativePath);

        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || IsReparsePoint(path))
            throw CatalogMismatch("The runtime font catalog path escapes its root.");
        return path;
    }

    private static string NormalizeDeclaredPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath) || relativePath.Contains('\0') ||
            relativePath.Contains('\\') || relativePath.Split('/', StringSplitOptions.None).Any(part => part is "" or "." or "..") ||
            !string.Equals(relativePath, relativePath.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
            throw CatalogMismatch("The runtime font catalog contains an invalid path.");
        return relativePath;
    }

    private static void EnsureDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw CatalogMismatch("The runtime font catalog is unavailable.");
    }

    private static void EnsureRegularFile(string path, string code)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new RuntimeFontBindingException(code, code == "font_catalog_mismatch"
                ? "The runtime font catalog is unavailable."
                : "The selected runtime font face is unavailable.");
    }

    private static bool IsReparsePoint(string path)
    {
        FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static RuntimeFontBindingException CatalogMismatch(string message, Exception? innerException = null) =>
        new("font_catalog_mismatch", message, innerException);

    private static RuntimeFontBindingException Unavailable(string message, Exception? innerException = null) =>
        new("runtime_font_unavailable", message, innerException);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; }
        public string? DefaultFaceId { get; set; }
        public List<CatalogItem>? Items { get; set; }
    }

    private sealed class CatalogItem
    {
        public string FaceId { get; set; } = string.Empty;
        public string RuntimeFamilyName { get; set; } = string.Empty;
        public string RuntimeTtfPath { get; set; } = string.Empty;
        public string RuntimeTtfSha256 { get; set; } = string.Empty;
        public long RuntimeTtfByteLength { get; set; }
        public string WebWoff2Path { get; set; } = string.Empty;
        public string WebWoff2Sha256 { get; set; } = string.Empty;
        public long WebWoff2ByteLength { get; set; }
    }
}
