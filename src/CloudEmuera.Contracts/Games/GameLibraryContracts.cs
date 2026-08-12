namespace CloudEmuera.Contracts.Games;

public sealed record CreateGameRequest(string Name, string Visibility = "PRIVATE");
public sealed record UpdateGameRequest(string? Name, string? Visibility);
public sealed record BindGamePackageRequest(string IngestionId, string ContentDigest);
public sealed record SetGameBlockedRequest(bool Blocked);
