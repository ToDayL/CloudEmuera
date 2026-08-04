using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Ports;

[Trait("Category", "RuntimePaths")]
public sealed class RuntimePortContractTests
{
    [Fact]
    public void AudioNoOpRecordsUnsupportedOperations()
    {
        var port = new NoOpRuntimeAudioPort();
        RuntimeFilePath path = RuntimeFilePath.Parse(RuntimeFileArea.GameContent, "resources/music.ogg");
        RuntimeAudioPlaybackResult result = port.Play(new RuntimeAudioRequest(path, loop: true));

        Assert.Equal(RuntimeAudioPlaybackResult.Unsupported, result);
        Assert.Single(port.PlayedRequests);
    }

    [Fact]
    public void ConsoleAndMediaPortsContainNoDesktopTypes()
    {
        Type[] publicTypes = typeof(IGameConsole).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(IGameConsole).Namespace)
            .ToArray();

        Assert.DoesNotContain(publicTypes, type =>
            type.Assembly.GetName().Name is "System.Drawing.Common" or "System.Windows.Forms" or "PresentationFramework" or "WindowsBase");
        Assert.DoesNotContain(typeof(IGameConsole).GetMethods(), method =>
            method.GetParameters().Any(parameter => IsDesktopType(parameter.ParameterType)) ||
            IsDesktopType(method.ReturnType));
    }

    private static bool IsDesktopType(Type type)
    {
        while (type.IsArray || type.IsByRef || type.IsPointer)
        {
            type = type.GetElementType()!;
        }

        return type.Namespace?.StartsWith("System.Drawing", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("System.Windows.Forms", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true;
    }
}
