// CloudEmuera modification: Linux headless Console implementation for the
// pinned upstream parser/process. No desktop control or message pump is used.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.GameView;

internal enum ConsoleRedraw { None, Normal }

internal sealed class EmueraConsole
{
    private readonly IGameConsole adapter;
    private readonly IRuntimeClock clock;
    private CancellationToken cancellationToken;
    private readonly Func<string, (string AssetId, int Width, int Height)?> imageResolver;
    private bool isRunning = true;
    private string windowTitle = string.Empty;
    private int generation;
    private StringStyle stringStyle;
    private readonly List<string> runtimeMessages = [];
    private bool outputEnabled;

    public EmueraConsole(
        IGameConsole adapter,
        IRuntimeClock clock,
        CancellationToken cancellationToken,
        Func<string, (string AssetId, int Width, int Height)?> imageResolver = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.cancellationToken = cancellationToken;
        this.imageResolver = imageResolver;
        stringStyle = new StringStyle(Color.Empty, FontStyle.Regular, string.Empty);
        GlobalStatic.Console = this;
    }

    public bool IsRunning => isRunning;
    public void SetCancellationToken(CancellationToken value) => cancellationToken = value;
    public void BeginExecutionOutput() => outputEnabled = true;
    public bool Enabled => isRunning;
    public bool IsActive => isRunning;
    public bool IsTimeOut => false;
    public bool MesSkip { get; set; }
    public bool AlwaysRefresh { get; set; }
    public bool UseUserStyle { get; set; }
    public bool UseSetColorStyle { get; set; }
    public bool noOutputLog;
    public bool updatedGeneration;
    public bool bitmapCacheEnabledForNextLine;
    public bool RunERBFromMemory { get; set; }
    public Color bgColor;
    public ConsoleRedraw Redraw => ConsoleRedraw.None;
    public DisplayLineAlignment Alignment { get; set; }
    public StringStyle StringStyle => stringStyle;
    public MainWindow Window { get; } = new();
    public List<ConsoleDisplayLine> DisplayLineList { get; } = [];
    public Dictionary<int, List<AConsoleDisplayNode>> EscapedParts { get; } = [];
    public PrintStringBuffer PrintBuffer => null;
    public StringMeasure StrMeasure => null;
    public ConsoleButtonString SelectingButton => null;
    public ConsoleButtonString PointingSring => null;
    public ConsoleButtonString[] bitmapCacheArray = new ConsoleButtonString[256];
    public const nint bitmapCacheArrayCap = 256;
    public nint bitmapCacheArrayIndex;
    public int LastButtonGeneration => generation;
    public int NewButtonGeneration => generation;
    public int GetLineNo => 0;
    public long LineCount => 0;
    public long DeletedLines => 0;
    public bool EmptyLine => true;
    public bool LastLineIsTemporary => false;
    public bool LastLineIsEmpty => false;
    public int ClientWidth => 0;
    public int ClientHeight => 0;
    public IReadOnlyList<string> RuntimeMessages => runtimeMessages;

    public void Print(string value, bool lineEnd = true) => EmitText(value);
    public void PrintSingleLine(string value) => PrintSingleLine(value, false);
    public void PrintSingleLine(string value, bool temporary) => EmitLine(value);
    public void PrintSystemLine(string value) => RecordMessage(value);
    public void PrintError(string value) => RecordMessage(value);
    public void PrintWarning(string value, ScriptPosition? position, int level) =>
        RecordMessage(FormatDiagnostic(value, position));
    public void PrintErrorButton(string value, ScriptPosition? position, int level = 0) =>
        RecordMessage(FormatDiagnostic(value, position));
    public void PrintTemporaryLine(string value) => EmitLine(value);
    public void PrintPlain(string value) => EmitText(value);
    public void PrintPlainWithSingleLineFix(string value) => EmitLine(value);
    public void PrintC(string value, bool alignmentRight) => EmitText(value);
    public void PrintButton(string value, string input) => EmitButton(value, input);
    public void PrintButton(string value, long input) => EmitButton(value, input.ToString(CultureInfo.InvariantCulture));
    public void PrintButtonC(string value, string input, bool isRight) => EmitButton(value, input);
    public void PrintButtonC(string value, long input, bool isRight) => EmitButton(value, input.ToString(CultureInfo.InvariantCulture));
    public void NewLine()
    {
        if (outputEnabled)
            adapter.Emit(new AppendNodesOperation([LineBreakNode.Instance]));
    }
    public void PrintFlush(bool force) { }
    public void RefreshStrings(bool forcePaint) { }
    public void ClearText() => adapter.Emit(new ClearConsoleOperation());
    public void ClearDisplay() => ClearText();
    public void deleteLine(int count) { }

    public void PrintHtml(string fragment, bool toPrintBuffer)
    {
        EmueraHtmlParseResult result = new EmueraHtmlParser().ParseWithDiagnostics(fragment);
        if (result.WasFailClosed)
            throw new NotSupportedException("The HTML fragment is outside the headless allowlist.");
        adapter.Emit(new AppendNodesOperation(result.Nodes.Concat<ConsoleNode>([LineBreakNode.Instance])));
    }

    public void PrintImg(string name, string nameb, string namem, MixedNum height, MixedNum width, MixedNum ypos)
    {
        (string AssetId, int Width, int Height)? resolved = imageResolver?.Invoke(name);
        if (resolved is null)
            throw new NotSupportedException($"Sprite '{name}' is unavailable in the headless runtime.");
        adapter.Emit(new AppendNodesOperation([
            new ImageNode(resolved.Value.AssetId, resolved.Value.Width, resolved.Value.Height)
        ]));
    }

    public void WaitInput(InputRequest request)
    {
        ConsoleInputType type = request.InputType is InputType.StrValue or InputType.StrButton
            ? ConsoleInputType.Text
            : ConsoleInputType.Integer;
        string defaultValue = request.HasDefValue
            ? type == ConsoleInputType.Integer ? request.DefIntValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : request.DefStrValue
            : null;
        TimeSpan? timeout = request.Timelimit > 0 ? TimeSpan.FromMilliseconds(request.Timelimit) : null;
        var prompt = new ConsolePrompt(type, defaultValue: defaultValue, timeout: timeout);
        GameConsoleInput input = adapter.Read(prompt, cancellationToken);
        if (type == ConsoleInputType.Integer)
        {
            long value = long.Parse(input.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (request.IsSystemInput)
                GlobalStatic.Process.InputSystemInteger(value);
            else
                GlobalStatic.Process.InputInteger(value);
        }
        else
        {
            GlobalStatic.Process.InputString(input.Value);
        }
    }

    public void ReadAnyKey(bool anykey = false, bool stopMesskip = false)
    {
        adapter.Read(new ConsolePrompt(ConsoleInputType.Text), cancellationToken);
    }

    public void Quit() => isRunning = false;
    public void ForceQuit() => Quit();
    public void ThrowError(bool playSound) => Quit();
    public void ThrowTitleError(bool error) => Quit();
    public void Await(int milliseconds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (milliseconds > 0)
            clock.DelayAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).AsTask().GetAwaiter().GetResult();
    }
    public void ResetStyle() => stringStyle = new StringStyle(Color.Empty, FontStyle.Regular, string.Empty);
    public void SetStringStyle(FontStyle style) => stringStyle.FontStyle = style;
    public void SetStringStyle(Color color) => stringStyle.Color = color;
    public void SetFont(string fontName) => stringStyle.Fontname = fontName;
    public void SetBgColor(Color color) => bgColor = color;
    public void SetWindowTitle(string value) => windowTitle = value ?? string.Empty;
    public string GetWindowTitle() => windowTitle;
    public void UpdateGeneration() => generation++;
    public void forceUpdateGeneration() => generation++;
    public bool ButtonIsSelected(ConsoleButtonString button) => false;
    public bool ButtonIsPointing(ConsoleButtonString button) => false;
    public Point GetMousePosition() => Point.Empty;
    public bool MoveMouse(Point point) => false;

    public ConsoleDisplayLine[] GetDisplayLines(long lineNo) => [];
    public ConsoleDisplayLine[] PopDisplayingLines() => [];
    public int GetLinePointY(int lineNo) => 0;
    public string getDefStBar() => "-";
    public string getStBar(string value) => value;
    public void setStBar(string value) { }
    public void PrintBar() => EmitLine("-");
    public void printCustomBar(string value, bool isConst) => EmitLine(value);
    public bool OutputLog(string filename, bool hideInfo) => false;
    public bool OutputSystemLog(string filename) => false;
    public void OutputLog(string filename) { }
    public void DebugPrint(string value) { }
    public void DebugClear() { }
    public void DebugNewLine() { }
    public void DebugAddTraceLog(string value) { }
    public void DebugRemoveTraceLog() { }
    public void DebugClearTraceLog() { }

    public void PrintShape(params object[] args) => Unsupported();
    public void PrintHTMLIsland(params object[] args) => Unsupported();
    public void ClearHTMLIsland() => Unsupported();
    public void AddBackgroundImage(params object[] args) => Unsupported();
    public void ClearBackgroundImage() => Unsupported();
    public void RemoveBackground(params object[] args) => Unsupported();
    public void CBG_Clear() => Unsupported();
    public void CBG_ClearRange(params object[] args) => Unsupported();
    public void CBG_ClearButton() => Unsupported();
    public void CBG_ClearBMap() => Unsupported();
    public bool CBG_SetGraphics(params object[] args) { Unsupported(); return false; }
    public bool CBG_SetImage(params object[] args) { Unsupported(); return false; }
    public bool CBG_SetButtonMap(params object[] args) { Unsupported(); return false; }
    public bool CBG_SetButtonImage(params object[] args) { Unsupported(); return false; }
    public void SetRedraw(params object[] args) { }
    public void setRedrawTimer(params object[] args) { }
    public void ReloadErbFinished() { }
    public void CustomToolTip(params object[] args) => Unsupported();
    public void SetToolTipColor(params object[] args) => Unsupported();
    public void SetToolTipDelay(params object[] args) => Unsupported();
    public void SetToolTipDuration(params object[] args) => Unsupported();
    public void SetToolTipFontName(params object[] args) => Unsupported();
    public void SetToolTipFontSize(params object[] args) => Unsupported();
    public void SetToolTipFormat(params object[] args) => Unsupported();
    public void SetToolTipImg(params object[] args) => Unsupported();

    private void EmitText(string value)
    {
        if (outputEnabled && !string.IsNullOrEmpty(value))
            adapter.Emit(new AppendNodesOperation([new TextNode(value)]));
    }

    private void EmitButton(string label, string input)
    {
        if (outputEnabled && !string.IsNullOrEmpty(label))
            adapter.Emit(new AppendNodesOperation([new ButtonNode(label, input)]));
    }

    private void EmitLine(string value)
    {
        if (!outputEnabled)
            return;
        if (string.IsNullOrEmpty(value))
            NewLine();
        else
            adapter.Emit(new AppendNodesOperation([new TextNode(value), LineBreakNode.Instance]));
    }

    private static void Unsupported() => throw new NotSupportedException("The upstream desktop capability is unavailable in headless mode.");

    private void RecordMessage(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            runtimeMessages.Add(value.Trim());
    }

    private static string FormatDiagnostic(string value, ScriptPosition? position) =>
        position is { } located
            ? $"{value} ({located.Filename}:{located.LineNo})"
            : value;
}
