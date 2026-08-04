using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.RuntimeAdapter;

public sealed record RuntimeAudioRequest
{
    public RuntimeAudioRequest(
        RuntimeFilePath resourcePath,
        bool loop = false,
        float volume = 1f)
    {
        if (resourcePath.Area != RuntimeFileArea.GameContent)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "Audio resources must be supplied from GameContent.",
                resourcePath.LogicalPath,
                resourcePath.Area);
        }

        if (float.IsNaN(volume) || float.IsInfinity(volume) || volume is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        ResourcePath = resourcePath;
        Loop = loop;
        Volume = volume;
    }

    public RuntimeFilePath ResourcePath { get; }

    public bool Loop { get; }

    public float Volume { get; }
}

public enum RuntimeAudioPlaybackResult
{
    Played = 0,
    Stopped = 1,
    Unsupported = 2
}

/// <summary>
/// Platform-neutral audio capability. Implementations decide whether media is
/// available; unsupported playback is returned and remains observable.
/// </summary>
[SuppressMessage("Naming", "CA1716", Justification = "Stop is the cross-platform audio capability name.")]
public interface IRuntimeAudioPort
{
    RuntimeAudioPlaybackResult Play(
        RuntimeAudioRequest request,
        CancellationToken cancellationToken = default);

    RuntimeAudioPlaybackResult Stop(
        RuntimeFilePath resourcePath,
        CancellationToken cancellationToken = default);

    void SetVolume(float volume, CancellationToken cancellationToken = default);
}

/// <summary>
/// Recording no-op audio adapter for headless tests. It never claims that
/// audio was played and records every request for assertions.
/// </summary>
public class RecordingRuntimeAudioPort : IRuntimeAudioPort
{
    private readonly List<RuntimeAudioRequest> playedRequests = [];
    private readonly List<RuntimeFilePath> stoppedPaths = [];
    private readonly List<float> volumeChanges = [];

    public IReadOnlyList<RuntimeAudioRequest> PlayedRequests => playedRequests;

    public IReadOnlyList<RuntimeFilePath> StoppedPaths => stoppedPaths;

    public IReadOnlyList<float> VolumeChanges => volumeChanges;

    public RuntimeAudioPlaybackResult Play(
        RuntimeAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        playedRequests.Add(request);
        return RuntimeAudioPlaybackResult.Unsupported;
    }

    public RuntimeAudioPlaybackResult Stop(
        RuntimeFilePath resourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stoppedPaths.Add(resourcePath);
        return RuntimeAudioPlaybackResult.Unsupported;
    }

    public void SetVolume(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (float.IsNaN(volume) || float.IsInfinity(volume) || volume is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        volumeChanges.Add(volume);
    }
}

public sealed class NoOpRuntimeAudioPort : RecordingRuntimeAudioPort
{
}
