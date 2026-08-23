// CloudEmuera headless platform boundary: compile-time desktop shapes are
// replaced with inert declarations; supported behavior is routed through ports.
using System;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

internal static class HeadlessDrawingBootstrap
{
    [ModuleInitializer]
    internal static void EnablePinnedUpstreamDrawing() =>
        AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);
}

namespace MinorShift.Emuera
{
    internal static class Program
    {
        public static string ExeDir = string.Empty;
        public static string CsvDir = string.Empty;
        public static string ErbDir = string.Empty;
        public static string DebugDir = string.Empty;
        public static string DatDir = string.Empty;
        public static string ContentDir = string.Empty;
        public static string SoundDir = string.Empty;
        public static string FontDir = string.Empty;
        public static string ExeName = "cloudemuera";
        public static bool AnalysisMode;
        public static bool DebugMode;
        public static bool rebootFlag;
        public static List<string> AnalysisFiles = [];

        public static void ConfigureHeadless(
            string executableRoot,
            string csvRoot,
            string erbRoot,
            string temporaryRoot,
            string contentRoot,
            string soundRoot,
            string fontRoot)
        {
            ExeDir = WithSeparator(executableRoot);
            CsvDir = WithSeparator(csvRoot);
            ErbDir = WithSeparator(erbRoot);
            DebugDir = WithSeparator(temporaryRoot);
            DatDir = WithSeparator(temporaryRoot);
            ContentDir = WithSeparator(contentRoot);
            SoundDir = WithSeparator(soundRoot);
            FontDir = WithSeparator(fontRoot);
            AnalysisMode = false;
            DebugMode = false;
            rebootFlag = false;
            AnalysisFiles = [];
        }

        private static string WithSeparator(string path) =>
            path.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? path
                : path + System.IO.Path.DirectorySeparatorChar;
    }
}

namespace MinorShift.Emuera.UI.Game
{
    internal sealed class HotkeyState
    {
        public void HotkeyStateSet(nint index, nint value) { }
        public void HotkeyStateInit(nint size) { }
    }
}
namespace MinorShift.Emuera.Forms
{
    internal sealed class MainWindow
    {
        public MinorShift.Emuera.UI.Game.HotkeyState hotkeyState { get; } = new();
        public HeadlessPictureBox MainPicBox { get; } = new();
        public HeadlessTextBox TextBox { get; } = new();
        public void ApplyTextBoxChanges() { }
        public void ChangeTextBox(string value) => TextBox.Text = value ?? string.Empty;
        public void ResetTextBoxPos() { }
        public void SetTextBoxPos(int x, int y, int width) { }
    }

    internal sealed class HeadlessPictureBox
    {
        public int Width => 800;
        public int Height => 600;
        public System.Drawing.Rectangle ClientRectangle => new(0, 0, Width, Height);
        public System.Drawing.Point PointToClient(System.Drawing.Point point) => point;
    }

    internal sealed class HeadlessTextBox
    {
        public string Text { get; set; } = string.Empty;
    }
}
namespace MinorShift.Emuera.Sub { }

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem
{
    internal sealed class PluginManager
    {
        private static readonly PluginManager Instance = new();
        public static PluginManager GetInstance() => Instance;
        public void SetParent(object process, object state, object mediator) { }
        public void LoadPlugins() { }
        public bool HasMethod(string name) => false;
        public IPluginMethod GetMethod(string name) => null;
        public object GetType(string name) => null;
    }
}

namespace MinorShift.Emuera.Runtime.Utils
{
    internal sealed class Sound
    {
        private CloudEmuera.RuntimeAdapter.RuntimeFilePath? currentPath;
        private bool playing;
        public void play(string filename, int repeat = 1)
        {
            currentPath = HeadlessAudioBridge.ToLogicalPath(filename);
            playing = HeadlessAudioBridge.Play(currentPath.Value, repeat < 0);
        }
        public void stop()
        {
            if (currentPath is not null)
                HeadlessAudioBridge.Stop(currentPath.Value);
            playing = false;
        }
        public void close() { }
        public bool isPlaying() => playing;
        public void setVolume(int volume) => HeadlessAudioBridge.SetVolume(volume);
    }

    internal static class HeadlessAudioBridge
    {
        private static CloudEmuera.RuntimeAdapter.IRuntimeAudioPort port;
        private static System.Threading.CancellationToken cancellationToken;
        public static bool UnsupportedRequested { get; private set; }

        public static void Configure(CloudEmuera.RuntimeAdapter.IRuntimeAudioPort value, System.Threading.CancellationToken token)
        {
            port = value;
            cancellationToken = token;
            UnsupportedRequested = false;
        }

        public static void SetCancellationToken(System.Threading.CancellationToken value) => cancellationToken = value;

        public static CloudEmuera.RuntimeAdapter.RuntimeFilePath ToLogicalPath(string filename)
        {
            string relative = System.IO.Path.GetRelativePath(MinorShift.Emuera.Program.SoundDir, filename)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                throw new CloudEmuera.RuntimeAdapter.RuntimeFileAccessException(
                    CloudEmuera.RuntimeAdapter.RuntimePathReasonCodes.PathOutsideArea,
                    "The audio path is outside the controlled sound root.",
                    relative,
                    CloudEmuera.RuntimeAdapter.RuntimeFileArea.GameContent);
            return new CloudEmuera.RuntimeAdapter.RuntimeFilePath(
                CloudEmuera.RuntimeAdapter.RuntimeFileArea.GameContent,
                $"sound/{relative}");
        }

        public static bool Play(CloudEmuera.RuntimeAdapter.RuntimeFilePath path, bool loop)
        {
            var result = port.Play(new CloudEmuera.RuntimeAdapter.RuntimeAudioRequest(path, loop), cancellationToken);
            UnsupportedRequested |= result == CloudEmuera.RuntimeAdapter.RuntimeAudioPlaybackResult.Unsupported;
            return result == CloudEmuera.RuntimeAdapter.RuntimeAudioPlaybackResult.Played;
        }

        public static void Stop(CloudEmuera.RuntimeAdapter.RuntimeFilePath path)
        {
            var result = port.Stop(path, cancellationToken);
            UnsupportedRequested |= result == CloudEmuera.RuntimeAdapter.RuntimeAudioPlaybackResult.Unsupported;
        }

        public static void SetVolume(int volume) =>
            port.SetVolume(Math.Clamp(volume / 1000f, 0f, 1f), cancellationToken);
    }
}

namespace System.Windows.Forms
{
    internal enum DialogResult { None, OK, Cancel, Yes, No }
    internal enum MessageBoxButtons { OK, YesNo }
    internal enum MessageBoxIcon { None }
    internal enum MessageBoxDefaultButton { Button1, Button2 }
    internal enum Keys { None }
    [Flags]
    internal enum TextFormatFlags { NoPadding = 1, NoPrefix = 2, PreserveGraphicsClipping = 4 }
    internal static class TextRenderer
    {
        public static System.Drawing.Size MeasureText(
            System.Drawing.Graphics graphics,
            ReadOnlySpan<char> text,
            System.Drawing.Font font,
            System.Drawing.Size proposedSize,
            TextFormatFlags flags)
        {
            if (graphics is null)
                throw new ArgumentNullException(nameof(graphics));
            if (font is null)
                throw new ArgumentNullException(nameof(font));
            if (text.IsEmpty)
                return System.Drawing.Size.Empty;

            // The old headless placeholder returned text.Length * 2/3 of the
            // font size. That made the Worker layout independent of the bound
            // TTF, so CJK, Latin, styled text, PRINTC padding, and alignment
            // all received fabricated widths. The selected TTF's cmap/hmtx
            // advance table is loaded once for this runtime; TextRenderer is
            // only the compatibility seam used by StringMeasure.
            using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
            {
                FormatFlags = System.Drawing.StringFormatFlags.MeasureTrailingSpaces |
                    System.Drawing.StringFormatFlags.NoClip |
                    System.Drawing.StringFormatFlags.NoWrap,
                Trimming = System.Drawing.StringTrimming.None,
            };
            System.Drawing.SizeF layoutSize = proposedSize.Width > 0 && proposedSize.Height > 0
                ? new(proposedSize.Width, proposedSize.Height)
                : new(float.MaxValue, float.MaxValue);
            string value = text.ToString();
            if (HeadlessFontMetrics.TryMeasure(text, font, out int authoritativeWidth))
            {
                int authoritativeHeight = proposedSize.Height > 0
                    ? proposedSize.Height
                    : checked((int)Math.Ceiling(Math.Max(0, font.GetHeight(graphics))));
                return new(authoritativeWidth, authoritativeHeight);
            }

            System.Drawing.SizeF measured = graphics.MeasureString(value, font, layoutSize, format);
            int width = checked((int)Math.Ceiling(Math.Max(0, measured.Width)));
            int height = checked((int)Math.Ceiling(Math.Max(0, measured.Height)));
            return new(width, height);
        }
        public static void DrawText(
            System.Drawing.Graphics graphics,
            ReadOnlySpan<char> text,
            System.Drawing.Font font,
            System.Drawing.Point point,
            System.Drawing.Color color,
            TextFormatFlags flags) { }
        public static void DrawText(
            System.Drawing.Graphics graphics,
            ReadOnlySpan<char> text,
            System.Drawing.Font font,
            System.Drawing.Point point,
            System.Drawing.Color color,
            System.Drawing.Color backColor,
            TextFormatFlags flags) { }
    }
    internal static class MessageBox
    {
        public static DialogResult Show(string text) => DialogResult.OK;
        public static DialogResult Show(string text, string caption) => DialogResult.OK;
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => DialogResult.No;
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => DialogResult.No;
    }
    internal static class Application
    {
        public static string ExecutablePath => string.Empty;
        public static string ProductVersion => "CloudEmuera-headless";
        public static void DoEvents() { }
    }

    internal static class Control
    {
        public static System.Drawing.Point MousePosition => System.Drawing.Point.Empty;
    }
}

namespace MinorShift.Emuera.Runtime.Utils
{
    internal sealed class WebP : IDisposable
    {
        public System.Drawing.Bitmap Load(string path) => null;
        public void Dispose() { }
    }
}

namespace System.Media
{
    internal sealed class SystemSound { public void Play() { } }
    internal static class SystemSounds
    {
        public static SystemSound Hand { get; } = new();
        public static SystemSound Asterisk { get; } = new();
    }
}
