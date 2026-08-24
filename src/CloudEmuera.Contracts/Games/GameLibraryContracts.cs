namespace CloudEmuera.Contracts.Games;

public sealed record UpdateGameRequest(string? Name, string? Visibility);
public sealed record SetGameBlockedRequest(bool Blocked);
