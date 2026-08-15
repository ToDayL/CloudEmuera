using System.IO;
using CloudEmuera.Application.Identity;

namespace CloudEmuera.Application.Assets;

public static class SessionAssetErrorCodes
{
    public const string NotFound = "SESSION_ASSET_NOT_FOUND";
    public const string Invalid = "SESSION_ASSET_INVALID";
    public const string StorageFailure = "SESSION_ASSET_STORAGE_FAILURE";
}

public sealed class SessionAssetException(
    string code,
    string message,
    int statusCode,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record SessionPresentationAsset(
    string AssetId,
    string MediaType,
    long ByteLength,
    string ContentDigest,
    string? ETag = null);

public sealed record SessionPresentationFont(
    string Family,
    string AssetId,
    string Fallback,
    string CssFamily,
    IReadOnlyList<string> Aliases);

public sealed record SessionPresentationManifest(
    int SchemaVersion,
    IReadOnlyList<SessionPresentationAsset> Assets,
    IReadOnlyList<SessionPresentationFont> Fonts,
    IReadOnlyList<string> FontDiagnostics);

public sealed record SessionAssetRead(
    string AssetId,
    string MediaType,
    long ByteLength,
    string ContentDigest,
    Stream Content);

public interface ISessionAssetService
{
    Task<SessionPresentationManifest> GetManifestAsync(
        CurrentActor actor,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionAssetRead> OpenReadAsync(
        CurrentActor actor,
        string sessionId,
        string assetId,
        CancellationToken cancellationToken = default);
}
