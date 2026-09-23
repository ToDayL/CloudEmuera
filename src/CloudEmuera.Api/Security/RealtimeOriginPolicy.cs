using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace CloudEmuera.Api.Security;

/// <summary>Optional exact Origin allowlist for browser realtime upgrades.</summary>
public sealed class RealtimeOriginPolicy
{
    private const string ConfigurationKey = "CloudEmuera:Realtime:AllowedOrigins";
    private readonly HashSet<string> allowedOrigins;

    private RealtimeOriginPolicy(HashSet<string> allowedOrigins) => this.allowedOrigins = allowedOrigins;

    public bool IsConfigured => allowedOrigins.Count > 0;

    public static RealtimeOriginPolicy Parse(string? configuredOrigins)
    {
        var normalizedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configuredOrigins))
            return new RealtimeOriginPolicy(normalizedOrigins);

        foreach (string configuredOrigin in configuredOrigins.Split(',', StringSplitOptions.None))
        {
            string candidate = configuredOrigin.Trim();
            if (!TryNormalizeOrigin(candidate, out string normalized))
                throw new InvalidOperationException($"{ConfigurationKey} must contain comma-separated http(s) origins without paths, queries, fragments, credentials, or wildcards.");

            normalizedOrigins.Add(normalized);
        }

        return new RealtimeOriginPolicy(normalizedOrigins);
    }

    public bool IsAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsConfigured)
            return true;

        Microsoft.Extensions.Primitives.StringValues headerValues = context.Request.Headers[HeaderNames.Origin];
        return headerValues.Count == 1 &&
            TryNormalizeOrigin(headerValues[0], out string normalized) &&
            allowedOrigins.Contains(normalized);
    }

    private static bool TryNormalizeOrigin(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !HasOriginOnlyShape(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.IdnHost.Contains('*') ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        normalized = string.Concat(
            uri.Scheme,
            "|",
            uri.IdnHost,
            "|",
            uri.Port.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool HasOriginOnlyShape(string value)
    {
        string withoutOptionalRootSlash = value.EndsWith('/')
            ? value[..^1]
            : value;
        int schemeSeparator = withoutOptionalRootSlash.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator <= 0 || withoutOptionalRootSlash.IndexOf("://", schemeSeparator + 3, StringComparison.Ordinal) >= 0)
            return false;

        string authority = withoutOptionalRootSlash[(schemeSeparator + 3)..];
        return authority.Length > 0 &&
            !authority.Contains('/') &&
            !authority.Contains('\\') &&
            !authority.Contains('?') &&
            !authority.Contains('#');
    }
}
