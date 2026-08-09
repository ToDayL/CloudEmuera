namespace CloudEmuera.Infrastructure.Persistence;

internal static class SqliteCheckExpressions
{
    public const string Id =
        "length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0";

    public const string Sha256Digest =
        "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)";

    public const string Json =
        "length({0}) BETWEEN 2 AND 1048576 AND json_valid({0}) = 1 AND {0} <> ''";

    public const string IdempotencyDigest =
        "length(request_digest) = 71 AND substr(request_digest, 1, 7) = 'sha256:' AND lower(request_digest) = request_digest AND substr(request_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(request_digest, 8)) = 64";

    public const string NonNegativeCounters =
        "state_version >= 0";

    public static string IdentifierPrefix(string column, string prefix) =>
        $"substr({column}, 1, {prefix.Length}) = '{prefix}' AND length({column}) BETWEEN 5 AND 64 AND instr({column}, char(0)) = 0";

    public static string RelativePath(string column) =>
        $"length({column}) BETWEEN 1 AND 512 AND substr({column}, 1, 1) <> '/' AND instr({column}, char(92)) = 0 AND instr({column}, char(0)) = 0 AND instr({column}, '//') = 0 AND instr('/' || {column} || '/', '/./') = 0 AND instr('/' || {column} || '/', '/../') = 0";

    public static string ValidJson(string column) =>
        $"length({column}) BETWEEN 2 AND 1048576 AND json_valid({column}) = 1 AND {column} <> ''";
}
