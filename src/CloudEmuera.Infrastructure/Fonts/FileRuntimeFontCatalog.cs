using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Fonts;

namespace CloudEmuera.Infrastructure.Fonts;

/// <summary>
/// Loads the immutable, image-owned runtime font catalog. The catalog is
/// deliberately not a general font directory: every path is declared by the
/// checked-in manifest and every asset is verified before it is opened.
/// </summary>
public sealed class FileRuntimeFontCatalog : IRuntimeFontCatalog
{
    public const string CatalogFileName = "catalog.json";
    public const string CatalogMismatchCode = "font_catalog_mismatch";
    public const string FontUnavailableCode = "runtime_font_unavailable";

    private readonly string root;
    private readonly IReadOnlyDictionary<string, RuntimeFontFace> faces;
    private readonly string catalogDigest;
    private static readonly IReadOnlyDictionary<string, (string Family, string SourceVersion, int Weight, string RuntimeFamilyName)> ExpectedFaces =
        new Dictionary<string, (string, string, int, string)>(StringComparer.Ordinal)
        {
            ["sarasa-fixed-sc-1.0.40-light"] = ("sarasa-fixed-sc", "1.0.40", 300, "Sarasa Fixed SC"),
            ["sarasa-fixed-sc-1.0.40-regular"] = ("sarasa-fixed-sc", "1.0.40", 400, "Sarasa Fixed SC"),
            ["sarasa-fixed-sc-1.0.40-medium"] = ("sarasa-fixed-sc", "1.0.40", 600, "Sarasa Fixed SC"),
            ["lxgw-wenkai-mono-1.522-light"] = ("lxgw-wenkai-mono", "1.522", 300, "LXGW WenKai Mono"),
            ["lxgw-wenkai-mono-1.522-regular"] = ("lxgw-wenkai-mono", "1.522", 400, "LXGW WenKai Mono"),
            ["lxgw-wenkai-mono-1.522-medium"] = ("lxgw-wenkai-mono", "1.522", 500, "LXGW WenKai Mono"),
        };

    public static string ResolveDefaultRoot()
    {
        string current = Path.GetFullPath(Directory.GetCurrentDirectory());
        for (DirectoryInfo? directory = new(current); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets", "runtime-fonts");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "runtime-fonts");
    }

    public FileRuntimeFontCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A runtime font root is required.", nameof(root));

        this.root = Path.GetFullPath(root);
        EnsureDirectory(this.root);
        string catalogPath = Path.Combine(this.root, CatalogFileName);
        EnsureRegularFile(catalogPath);
        byte[] catalogBytes = File.ReadAllBytes(catalogPath);
        catalogDigest = Sha256(catalogBytes);

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(catalogBytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog JSON is invalid.", exception);
        }

        if (document is null || document.SchemaVersion != 1 || document.Items is null || document.Items.Count != 6 ||
            !string.Equals(document.DefaultFaceId, RuntimeFontDefaults.DefaultFaceId, StringComparison.Ordinal))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog has no faces.");

        var parsed = new Dictionary<string, RuntimeFontFace>(StringComparer.Ordinal);
        var declaredAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogItem item in document.Items)
        {
            RuntimeFontFace face = ToFace(item);
            if (!parsed.TryAdd(face.FaceId, face))
                throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font face IDs must be unique.");
            ValidateDeclaredPath(face.RuntimeTtfPath, face.FaceId, "runtimeTtfPath");
            ValidateDeclaredPath(face.WebWoff2Path, face.FaceId, "webWoff2Path");
            ValidateDeclaredPath(face.LicenseFile, face.FaceId, "licenseFile");
            if (!declaredAssetPaths.Add(face.RuntimeTtfPath) || !declaredAssetPaths.Add(face.WebWoff2Path))
                throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font assets must not share paths.");
        }

        if (!parsed.ContainsKey(RuntimeFontDefaults.DefaultFaceId) || !ExpectedFaces.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(parsed.Keys))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog does not contain the default face.");
        faces = parsed;
    }

    public string CatalogDigest => catalogDigest;

    public IReadOnlyList<RuntimeFontFace> ListAvailable() => faces.Values
        .OrderBy(face => face.Family, StringComparer.Ordinal)
        .ThenBy(face => face.Weight)
        .ThenBy(face => face.FaceId, StringComparer.Ordinal)
        .ToArray();

    public RuntimeFontFace Require(string faceId)
    {
        if (string.IsNullOrWhiteSpace(faceId) || !faces.TryGetValue(faceId, out RuntimeFontFace? face))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "The requested runtime font face is not in the catalog.");
        return face;
    }

    public string GetRuntimeTtfPath(string faceId)
    {
        RuntimeFontFace face = Require(faceId);
        string path = ResolveDeclaredPath(face.RuntimeTtfPath, face.FaceId);
        VerifyAsset(path, face.RuntimeTtfSha256, face.RuntimeTtfByteLength, "TTF", face.FaceId);
        return path;
    }

    public FileStream OpenWebWoff2(string webAssetDigest)
    {
        if (!IsLowerHexSha256(webAssetDigest))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "The web font digest is invalid.");

        RuntimeFontFace? face = faces.Values.SingleOrDefault(item =>
            string.Equals(item.WebWoff2Sha256, webAssetDigest, StringComparison.Ordinal));
        if (face is null)
            throw new RuntimeFontCatalogException(FontUnavailableCode, "The web font asset is not in the catalog.");

        string path = ResolveDeclaredPath(face.WebWoff2Path, face.FaceId);
        VerifyAsset(path, face.WebWoff2Sha256, face.WebWoff2ByteLength, "WOFF2", face.FaceId);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
        });
    }

    public void VerifyAllAssets()
    {
        foreach (RuntimeFontFace face in faces.Values)
        {
            string ttf = ResolveDeclaredPath(face.RuntimeTtfPath, face.FaceId);
            VerifyAsset(ttf, face.RuntimeTtfSha256, face.RuntimeTtfByteLength, "TTF", face.FaceId);
            string woff2 = ResolveDeclaredPath(face.WebWoff2Path, face.FaceId);
            VerifyAsset(woff2, face.WebWoff2Sha256, face.WebWoff2ByteLength, "WOFF2", face.FaceId);
            string license = ResolveDeclaredPath(face.LicenseFile, face.FaceId);
            EnsureRegularFile(license);
        }
    }

    private string ResolveDeclaredPath(string relativePath, string faceId)
    {
        ValidateDeclaredPath(relativePath, faceId, "assetPath");
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || IsReparsePoint(path))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "A runtime font catalog path escapes its root.");
        return path;
    }

    private static RuntimeFontFace ToFace(CatalogItem item)
    {
        if (item is null || !ExpectedFaces.TryGetValue(item.FaceId, out (string Family, string SourceVersion, int Weight, string RuntimeFamilyName) expected))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog contains an unsupported face.");
        if (item is null || string.IsNullOrWhiteSpace(item.FaceId) || item.FaceId.Length > 128 ||
            item.FaceId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~')) ||
            string.IsNullOrWhiteSpace(item.DisplayName) ||
            !string.Equals(item.Family, expected.Family, StringComparison.Ordinal) ||
            !string.Equals(item.SourceVersion, expected.SourceVersion, StringComparison.Ordinal) || item.Weight != expected.Weight ||
            string.IsNullOrWhiteSpace(item.RuntimeFamilyName) || string.IsNullOrWhiteSpace(item.RuntimeTtfPath) ||
            string.IsNullOrWhiteSpace(item.RuntimeTtfSha256) || string.IsNullOrWhiteSpace(item.WebWoff2Path) ||
            string.IsNullOrWhiteSpace(item.WebWoff2Sha256) || string.IsNullOrWhiteSpace(item.LicenseId) ||
            string.IsNullOrWhiteSpace(item.LicenseFile) || item.RuntimeTtfByteLength <= 0 || item.WebWoff2ByteLength <= 0)
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog metadata is incomplete.");

        if (!string.Equals(item.RuntimeFamilyName, expected.RuntimeFamilyName, StringComparison.Ordinal))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font catalog family metadata is unsupported.");

        ValidateDigest(item.RuntimeTtfSha256);
        ValidateDigest(item.WebWoff2Sha256);
        return new RuntimeFontFace(
            item.FaceId,
            item.DisplayName,
            item.Family,
            item.SourceVersion,
            item.Weight,
            item.RuntimeFamilyName,
            item.RuntimeTtfPath,
            item.RuntimeTtfSha256,
            item.RuntimeTtfByteLength,
            item.WebWoff2Path,
            item.WebWoff2Sha256,
            item.WebWoff2ByteLength,
            item.LicenseId,
            item.LicenseFile);
    }

    private static void ValidateDeclaredPath(string value, string faceId, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value) || value.Contains('\0') ||
            value.Split('/', StringSplitOptions.None).Any(part => part is "" or "." or "..") ||
            value.Contains('\\') || !string.Equals(value, value.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
            throw new RuntimeFontCatalogException(FontUnavailableCode, $"Runtime font {field} is invalid for '{faceId}'.");
    }

    private static void ValidateDigest(string value)
    {
        if (!IsLowerHexSha256(value))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "Runtime font digests must be lowercase SHA-256 values.");
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void VerifyAsset(string path, string digest, long byteLength, string kind, string faceId)
    {
        EnsureRegularFile(path);
        FileInfo info = new(path);
        if (info.Length != byteLength)
            throw new RuntimeFontCatalogException(FontUnavailableCode, $"The {kind} byte length for '{faceId}' does not match the catalog.");
        string actual = Sha256(File.ReadAllBytes(path));
        if (!string.Equals(actual, digest, StringComparison.Ordinal))
            throw new RuntimeFontCatalogException(FontUnavailableCode, $"The {kind} digest for '{faceId}' does not match the catalog.");
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void EnsureDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "The runtime font root is unavailable.");
    }

    private static void EnsureRegularFile(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new RuntimeFontCatalogException(FontUnavailableCode, "A runtime font asset is not a regular file.");
    }

    private static bool IsReparsePoint(string path)
    {
        FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

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
        public string DisplayName { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string SourceVersion { get; set; } = string.Empty;
        public int Weight { get; set; }
        public string RuntimeFamilyName { get; set; } = string.Empty;
        public string RuntimeTtfPath { get; set; } = string.Empty;
        public string RuntimeTtfSha256 { get; set; } = string.Empty;
        public long RuntimeTtfByteLength { get; set; }
        public string WebWoff2Path { get; set; } = string.Empty;
        public string WebWoff2Sha256 { get; set; } = string.Empty;
        public long WebWoff2ByteLength { get; set; }
        public string LicenseId { get; set; } = string.Empty;
        public string LicenseFile { get; set; } = string.Empty;
    }
}
