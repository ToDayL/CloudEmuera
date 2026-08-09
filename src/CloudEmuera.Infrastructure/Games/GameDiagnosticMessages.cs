namespace CloudEmuera.Infrastructure.Games;

/// <summary>
/// Resolves persisted validation diagnostics (which only store a message key)
/// back to a human-readable message for the diagnostics API. Messages must stay
/// in sync with the transient diagnostics produced by <c>InspectWorkspace</c> and
/// the one-shot Validator.
/// </summary>
internal static class GameDiagnosticMessages
{
    private static readonly Dictionary<string, string> Catalog = new(StringComparer.Ordinal)
    {
        ["ERB_ENTRYPOINT_MISSING"] = "ERB directory must contain at least one .ERB file at the package root.",
        ["TEXT_ENCODING_UNSUPPORTED"] = "Text must be valid UTF-8 or CP932.",
        ["CALLSHARP_FORBIDDEN"] = "CALLSHARP is not allowed in server games.",
        ["GAME_CONTENT_ENTRY_LIMIT"] = "The game content exceeds the entry limit.",
        ["GAME_CONTENT_DEPTH_LIMIT"] = "The game content exceeds the directory depth limit.",
        ["GAME_CONTENT_FILE_LIMIT"] = "A single file exceeds the size limit.",
        ["GAME_CONTENT_TOTAL_LIMIT"] = "The game content exceeds the total size limit.",
        ["MISSING_RESOURCE"] = "A resource referenced by the game is missing.",
        ["OPTIONAL_RESOURCE_MISSING"] = "An optional resource referenced by the game is missing.",
        ["RESOURCE_CASE_MISMATCH"] = "A resource reference differs only in letter case.",
        ["RUNTIME_WARNING"] = "The Emuera parser reported a non-fatal script warning.",
    };

    public static string Resolve(string code, string? path, string fallback)
    {
        if (!Catalog.TryGetValue(code, out string? message)) return fallback;
        return path is null ? message : $"{message} ({path})";
    }
}
