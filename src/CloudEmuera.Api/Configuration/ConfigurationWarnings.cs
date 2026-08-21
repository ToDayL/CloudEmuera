using Microsoft.Extensions.Logging;

namespace CloudEmuera.Api.Configuration;

internal static partial class ConfigurationWarnings
{
    [LoggerMessage(
        EventId = 2107,
        Level = LogLevel.Warning,
        Message = "Configuration key CloudEmuera:Capacity:MaxGamePackageBytes is deprecated; use CloudEmuera:Capacity:MaxArchiveBytes instead.")]
    public static partial void LegacyMaxGamePackageBytes(ILogger logger);

    [LoggerMessage(
        EventId = 2108,
        Level = LogLevel.Warning,
        Message = "Configuration key CloudEmuera:MinDataRootFreeBytes is deprecated; use CloudEmuera:Capacity:MinDataRootFreeBytes instead.")]
    public static partial void LegacyMinDataRootFreeBytes(ILogger logger);
}
