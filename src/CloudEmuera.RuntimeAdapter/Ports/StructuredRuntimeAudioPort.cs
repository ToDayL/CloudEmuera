using System.Security.Cryptography;
using System.Text;

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
        if (fileSystem is not null && !fileSystem.FileExists(request.ResourcePath, cancellationToken))
            return RuntimeAudioPlaybackResult.Unsupported;

        ConsoleAssetId assetId = ToAssetId(request.ResourcePath, cancellationToken);
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
        ConsoleAssetId assetId = ToAssetId(resourcePath, cancellationToken);
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

    private ConsoleAssetId ToAssetId(RuntimeFilePath resourcePath, CancellationToken cancellationToken)
    {
        if (fileSystem is null)
        {
            string logicalDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourcePath.LogicalPath))).ToLowerInvariant();
            return new ConsoleAssetId($"logical-{logicalDigest}");
        }
        using Stream stream = fileSystem.OpenRead(resourcePath, cancellationToken);
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new ConsoleAssetId($"sha256-{digest}");
    }
}
