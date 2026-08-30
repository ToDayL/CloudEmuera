namespace CloudEmuera.Contracts;

public sealed record BuildInfo(
    string Product,
    string Version,
    string Runtime,
    int HttpProtocolVersion,
    int RealtimeProtocolVersion,
    int IpcProtocolVersion,
    string RealtimePayloadSchemaVersion = "p1-s10-button-generation");

public sealed record VersionResponse(
    string Product,
    string Version,
    string? Commit,
    string Runtime,
    int HttpApiSchemaVersion,
    int RealtimeEnvelopeVersion,
    string RealtimePayloadSchemaVersion,
    int WorkerIpcMajor,
    string RuntimeIntegrationVersion,
    string UpstreamCommit,
    string DatabaseSchemaCompatibilityVersion,
    string FontCatalogDigest = "");
