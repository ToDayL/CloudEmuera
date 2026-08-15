namespace CloudEmuera.Contracts.Saves;

public sealed record SaveListResponse(
    int SchemaVersion,
    string Layout,
    IReadOnlyList<SaveItemResponse> Items);

public sealed record SaveItemResponse(
    string Path,
    string Kind,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record RenameSaveRequest(string TargetPath);
