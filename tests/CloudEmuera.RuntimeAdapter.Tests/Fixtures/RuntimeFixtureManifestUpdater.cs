using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

public static class RuntimeFixtureManifestUpdater
{
    public static IReadOnlyList<string> UpdateHashes(string fixtureRoot)
    {
        var errors = new List<string>();
        string root;
        try
        {
            root = Path.GetFullPath(fixtureRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return [$"<root>: invalid fixture root: {exception.Message}"];
        }

        string manifestPath = Path.Combine(root, "manifest.json");
        if (!Directory.Exists(root))
        {
            return [$"<root>: directory does not exist: {root}"];
        }

        if (!File.Exists(manifestPath))
        {
            return ["manifest.json: file is missing"];
        }

        JsonObject document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject
                ?? throw new JsonException("root object is required");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [$"manifest.json: unable to parse JSON: {exception.Message}"];
        }

        if (document["fixtures"] is not JsonArray fixtureNodes)
        {
            return ["manifest.json: fixtures array is required"];
        }

        foreach (JsonNode? fixtureNode in fixtureNodes)
        {
            if (fixtureNode is not JsonObject fixtureObject)
            {
                errors.Add("manifest.json: every fixture must be an object");
                continue;
            }

            string? gameRoot = fixtureObject["gameRoot"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                errors.Add("manifest.json: fixture gameRoot is required for --update");
                continue;
            }

            if (fixtureObject["files"] is not JsonArray fileNodes)
            {
                errors.Add($"{gameRoot}: files array is required for --update");
                continue;
            }

            foreach (JsonNode? fileNode in fileNodes)
            {
                if (fileNode is not JsonObject fileObject)
                {
                    errors.Add($"{gameRoot}: every file must be an object");
                    continue;
                }

                string? relativePath = fileObject["path"]?.GetValue<string>();
                if (!TryResolvePayloadPath(root, relativePath, out string? absolutePath, out string? pathError))
                {
                    errors.Add($"{gameRoot}: {pathError}");
                    continue;
                }

                if (!File.Exists(absolutePath))
                {
                    errors.Add($"{relativePath}: missing file");
                    continue;
                }

                try
                {
                    using FileStream stream = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fileObject["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{relativePath}: unable to hash file: {exception.Message}");
                }
            }
        }

        errors.Sort(StringComparer.Ordinal);
        if (errors.Count > 0)
        {
            return errors;
        }

        try
        {
            string json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"manifest.json: unable to write updated hashes: {exception.Message}"];
        }

        return [];
    }

    private static bool TryResolvePayloadPath(
        string root,
        string? relativePath,
        out string? absolutePath,
        out string? error)
    {
        absolutePath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.Contains('\0') ||
            relativePath.StartsWith('/') ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = $"invalid relative payload path: {relativePath ?? "<missing>"}";
            return false;
        }

        string rootFull = Path.GetFullPath(root);
        absolutePath = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = $"payload path escapes fixture root: {relativePath}";
            absolutePath = null;
            return false;
        }

        return true;
    }
}
