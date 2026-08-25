namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Converts upstream audio requests into bounded media-state transactions.
/// It never opens a device and never exposes the logical source path on the
/// structured protocol.
/// </summary>
public sealed class StructuredRuntimeAudioPort : IRuntimeAudioPort
{
    private readonly StructuredGameConsole console;
    private readonly IRuntimeFileSystem? fileSystem;

    public StructuredRuntimeAudioPort(StructuredGameConsole console, IRuntimeFileSystem? fileSystem = null)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.fileSystem = fileSystem;
    }

    public RuntimeAudioPlaybackResult Play(
        RuntimeAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeFilePath assetPath = request.ResourcePath;
        if (fileSystem is not null)
        {
            if (!fileSystem.FileExists(assetPath, cancellationToken))
                return RuntimeAudioPlaybackResult.Unsupported;
            assetPath = fileSystem.ResolveExistingPath(assetPath, cancellationToken);
        }

        ConsoleAssetId assetId = ToAssetId(assetPath);
        MediaChannelState? previous = console.Snapshot.MediaState.Channels
            .FirstOrDefault(channel => string.Equals(channel.Channel, request.Channel, StringComparison.Ordinal));
        long revision = previous is null ? 1 : checked(previous.Revision + 1);
        console.EmitTransaction(new ConsoleTransaction([
            ConsoleOperation.SetMediaChannel(new MediaChannelState(
                request.Channel,
                assetId,
                ConsoleMediaPlaybackState.Requested,
                request.Loop,
                request.Volume,
                revision,
                request.StartPolicy switch
                {
                    RuntimeAudioStartPolicy.Immediate => ConsoleMediaStartPolicy.Immediate,
                    RuntimeAudioStartPolicy.OnUserGesture => ConsoleMediaStartPolicy.OnUserGesture,
                    _ => throw new ArgumentOutOfRangeException(nameof(request))
                }))
        ]));
        return RuntimeAudioPlaybackResult.Played;
    }

    public RuntimeAudioPlaybackResult Stop(
        RuntimeFilePath resourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeFilePath assetPath = resourcePath;
        if (fileSystem is not null && fileSystem.FileExists(assetPath, cancellationToken))
            assetPath = fileSystem.ResolveExistingPath(assetPath, cancellationToken);
        ConsoleAssetId assetId = ToAssetId(assetPath);
        string[] channels = console.Snapshot.MediaState.Channels
            .Where(channel => channel.AssetId == assetId)
            .Select(channel => channel.Channel)
            .ToArray();
        if (channels.Length == 0)
            return RuntimeAudioPlaybackResult.Stopped;

        console.EmitTransaction(new ConsoleTransaction(channels.Select(ConsoleOperation.StopMediaChannel)));
        return RuntimeAudioPlaybackResult.Stopped;
    }

    public void SetVolume(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackgroundLayer.ValidateOpacity(volume);
        MediaChannelState[] channels = console.Snapshot.MediaState.Channels.ToArray();
        if (channels.Length == 0)
            return;

        ConsoleOperation[] updates = channels.Select(channel =>
            ConsoleOperation.SetMediaChannel(new MediaChannelState(
                channel.Channel,
                channel.AssetId,
                channel.PlaybackState,
                channel.Loop,
                volume,
                checked(channel.Revision + 1),
                channel.StartPolicy))).ToArray();
        console.EmitTransaction(new ConsoleTransaction(updates));
    }

    public void StopAll(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (console.Snapshot.MediaState.Channels.Count != 0)
            console.EmitTransaction(new ConsoleTransaction([ConsoleOperation.StopAllMedia()]));
    }

    private static ConsoleAssetId ToAssetId(RuntimeFilePath resourcePath) =>
        new(ConsoleAssetIdCodec.EncodePath(resourcePath.LogicalPath));
}
