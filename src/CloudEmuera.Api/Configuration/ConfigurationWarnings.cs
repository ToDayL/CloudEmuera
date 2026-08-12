using Microsoft.Extensions.Logging;

namespace CloudEmuera.Api.Configuration;

internal static partial class ConfigurationWarnings
{
    [LoggerMessage(
        EventId = 2108,
        Level = LogLevel.Warning,
        Message = "Configuration key CloudEmuera:MinDataRootFreeBytes is deprecated; use CloudEmuera:Capacity:MinDataRootFreeBytes instead.")]
    public static partial void LegacyMinDataRootFreeBytes(ILogger logger);
}
