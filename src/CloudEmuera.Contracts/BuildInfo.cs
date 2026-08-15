namespace CloudEmuera.Contracts;

public sealed record BuildInfo(
    string Product,
    string Version,
    string Runtime,
    int HttpProtocolVersion,
    int RealtimeProtocolVersion,
    int IpcProtocolVersion,
    string RealtimePayloadSchemaVersion = "p1-11");
