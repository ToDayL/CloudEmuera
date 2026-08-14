namespace CloudEmuera.Api.Bootstrap;

/// <summary>
/// Resolves the browser HTTP listener without silently discarding the normal
/// ASP.NET Core URL configuration when the API also exposes its Worker UDS.
/// An explicit CloudEmuera:HttpPort takes precedence over ASPNETCORE_URLS.
/// </summary>
public static class KestrelHttpPortResolver
{
    public const int DefaultPort = 28647;

    public static int Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? explicitPort = configuration["CloudEmuera:HttpPort"];
        if (!string.IsNullOrWhiteSpace(explicitPort))
            return ParsePort(explicitPort, "CloudEmuera:HttpPort");

        string? urls = configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls))
            urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (string candidate in urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryCreateAspNetCoreUri(candidate, out Uri uri) ||
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    continue;
                return ParsePort(uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "ASPNETCORE_URLS");
            }
        }

        return DefaultPort;
    }

    private static bool TryCreateAspNetCoreUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) && parsed is not null)
        {
            uri = parsed;
            return true;
        }

        // ASP.NET Core also accepts the wildcard hosts used by the compose
        // and Kestrel URL syntax (for example, http://+:28647). Uri rejects
        // those hosts, but they carry the same port information.
        const string httpPrefix = "http://";
        if (value.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string hostAndPort = value[httpPrefix.Length..];
            if (hostAndPort.StartsWith("+:", StringComparison.Ordinal) ||
                hostAndPort.StartsWith("*:", StringComparison.Ordinal))
            {
                string normalized = httpPrefix + "localhost" + hostAndPort[1..];
                if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? normalizedUri) && normalizedUri is not null)
                {
                    uri = normalizedUri;
                    return true;
                }
            }
        }

        uri = null!;
        return false;
    }

    private static int ParsePort(string value, string name)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int port) || port is < 0 or > 65535)
            throw new InvalidOperationException($"{name} must contain an HTTP port between 0 and 65535.");
        return port;
    }
}
