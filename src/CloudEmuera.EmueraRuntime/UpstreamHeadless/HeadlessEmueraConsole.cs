// CloudEmuera modification: Linux headless Console implementation for the
// pinned upstream parser/process. No desktop control or message pump is used.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.GameView;

internal enum ConsoleRedraw { None, Normal }

internal sealed class EmueraConsole
{
    private static readonly Encoding printCByteCountEncoding = CreatePrintCByteCountEncoding();
    private readonly IGameConsole adapter;
    private readonly IRuntimeClock clock;
    private CancellationToken cancellationToken;
    private readonly Func<string, RuntimeSpriteDefinition> imageResolver;
    private readonly int viewportWidth;
    private readonly int viewportHeight;
    private string barString = "-";
    private bool isRunning = true;
    private bool hasFatalError;
    private bool isTimeOut;
    private string windowTitle = string.Empty;
    private int generation;
    private long lineId;
    private long logicalLineCount;
    private long deletedLines;
    private string? lastLineId;
    private bool lastLineCanAppend;
    private bool lastLineTemporary;
    private string? htmlIslandDrawableId;
    private int redrawIntervalMilliseconds;
    private long canvasDrawableId;
    private readonly List<ConsoleNode> pendingLine = [];
    private readonly List<PendingBufferedLine> pendingBufferedLines = [];
    private ConsoleLineAlignment? pendingLineAlignment;
    private bool pendingLineNoWrap;
    private bool pendingLineEnd = true;
    private StringStyle stringStyle;
    private readonly List<string> runtimeMessages = [];
    private readonly List<string> runtimeWarnings = [];
    private readonly List<string> runtimeSystemMessages = [];
    private readonly List<string> runtimeDebugMessages = [];
    private bool outputEnabled;

    private sealed record PendingBufferedLine(
        IReadOnlyList<ConsoleNode> Nodes,
        ConsoleLineAlignment? Alignment,
        bool NoWrap,
        bool LineEnd);

    public EmueraConsole(
        IGameConsole adapter,
        IRuntimeClock clock,
        CancellationToken cancellationToken,
        Func<string, RuntimeSpriteDefinition> imageResolver = null,
        int viewportWidth = 800,
        int viewportHeight = 600)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.cancellationToken = cancellationToken;
        this.imageResolver = imageResolver;
        if (viewportWidth <= 0 || viewportHeight <= 0 || viewportWidth > 8_192 || viewportHeight > 8_192)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "The logical headless viewport is outside its limit.");
        this.viewportWidth = viewportWidth;
        this.viewportHeight = viewportHeight;
        stringStyle = new StringStyle(Config.ForeColor, FontStyle.Regular, string.Empty);
        GlobalStatic.Console = this;
    }

    public bool IsRunning => isRunning;
    public bool HasFatalError => hasFatalError;
    public void SetCancellationToken(CancellationToken value) => cancellationToken = value;
    public void BeginExecutionOutput()
    {
        outputEnabled = true;
        if (adapter is StructuredGameConsole)
            EmitStructured(ConsoleOperation.SetWindow(new WindowMetadata(windowTitle, viewportWidth, viewportHeight)));
    }
    public bool Enabled => isRunning;
    public bool IsActive => isRunning;
    public bool IsTimeOut => isTimeOut || adapter is StructuredGameConsole structured && structured.IsTimeOut;
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
    public int GetLineNo => checked((int)logicalLineCount);
    public long LineCount => logicalLineCount;
    public long DeletedLines => deletedLines;
    public bool EmptyLine => pendingLine.Count == 0;
    public bool LastLineIsTemporary => lastLineTemporary;
    public bool LastLineIsEmpty => pendingLine.Count == 0;
    public int ClientWidth => viewportWidth;
    public int ClientHeight => viewportHeight;
    public IReadOnlyList<string> RuntimeMessages => runtimeMessages;
    public IReadOnlyList<string> RuntimeWarnings => runtimeWarnings;
    public IReadOnlyList<string> RuntimeSystemMessages => runtimeSystemMessages;
    public IReadOnlyList<string> RuntimeDebugMessages => runtimeDebugMessages;

    public void Print(string value, bool lineEnd = true)
    {
        if (!outputEnabled || string.IsNullOrEmpty(value))
            return;

        int lineEndIndex = value.IndexOf('\n', StringComparison.Ordinal);
        if (lineEndIndex >= 0)
        {
            AppendText(value[..lineEndIndex]);
            pendingLineEnd = true;
            NewLine();
            if (lineEndIndex < value.Length - 1)
                Print(value[(lineEndIndex + 1)..]);
            return;
        }

        AppendText(value);
        pendingLineEnd = lineEnd;
    }
    public void PrintSingleLine(string value) => PrintSingleLine(value, false);
    public void PrintSingleLine(string value, bool temporary) => EmitLine(value, temporary);
    // Upstream status/progress output (for example the DEBUG-only elapsed-time
    // reports) must not be treated as script diagnostics. Warnings are recorded
    // separately from errors so the headless session only gates activation on
    // real errors. Warnings remain compatibility diagnostics and are not
    // written into the player's console transcript.
    public void PrintSystemLine(string value) => RecordSystemMessage(value);
    public void PrintError(string value)
    {
        RecordMessage(value);
        EmitDiagnosticLine(value);
    }
    public void PrintWarning(string value, ScriptPosition? position, int level) =>
        RecordWarning(FormatDiagnostic(value, position));
    public void PrintErrorButton(string value, ScriptPosition? position, int level = 0) =>
        RecordMessageAndDisplay(FormatDiagnostic(value, position));
    public void PrintTemporaryLine(string value) => EmitLine(value, temporary: true);
    public void PrintPlain(string value) => EmitText(value);
    public void PrintPlainWithSingleLineFix(string value) => EmitLine(value);
    public void PrintC(string value, bool alignmentRight)
    {
        if (!outputEnabled || string.IsNullOrEmpty(value))
            return;
        // Upstream PRINTC appends a fixed-width field to PrintStringBuffer. It
        // does not commit a display line; PRINTL/PrintFlush owns that boundary.
        pendingLine.Add(new TextNode(FormatPrintCValue(value, alignmentRight), ToConsoleTextStyle()));
        pendingLineEnd = true;
    }
    public void PrintButton(string value, string input) => EmitButton(value, input);
    public void PrintButton(string value, long input) => EmitButton(value, input.ToString(CultureInfo.InvariantCulture));
    public void PrintButtonC(string value, string input, bool isRight) => EmitButton(FormatPrintCValue(value, isRight), input);
    public void PrintButtonC(string value, long input, bool isRight) => EmitButton(FormatPrintCValue(value, isRight), input.ToString(CultureInfo.InvariantCulture));
    public void NewLine()
    {
        if (outputEnabled)
        {
            pendingLineEnd = true;
            FlushPendingLine(force: true);
        }
    }
    public void PrintFlush(bool force) => FlushPendingLine(force);
    // Upstream RefreshStrings only repaints already committed display lines;
    // it must not turn a partial PRINT/PRINTC buffer into a logical line.
    public void RefreshStrings(bool forcePaint) { }
    public void ClearText()
    {
        FlushPendingLine();
        EmitStructured(ConsoleOperation.ClearConsole());
        lastLineId = null;
        lastLineCanAppend = false;
        lastLineTemporary = false;
    }
    public void ClearDisplay() => ClearText();
    public void deleteLine(int count)
    {
        if (count <= 0 || adapter is not StructuredGameConsole structured)
            return;
        string[] ids = structured.Snapshot.Scrollback
            .TakeLast(Math.Min(count, structured.Snapshot.Scrollback.Count))
            .Select(line => line.LineId)
            .ToArray();
        if (ids.Length == 0)
            return;
        try
        {
            EmitStructured(ConsoleOperation.DeleteLines(ids));
            deletedLines = checked(deletedLines + ids.Length);
            ConsoleLine? remaining = structured.Snapshot.Scrollback.LastOrDefault();
            lastLineId = remaining?.LineId;
            lastLineCanAppend = false;
            lastLineTemporary = remaining?.Temporary ?? false;
            logicalLineCount = Math.Max(0, logicalLineCount - ids.Length);
        }
        catch (ConsoleContractException)
        {
            // A line trimmed by the bounded scrollback is already absent.
        }
    }

    private UpstreamHtmlTranslationResult TranslateHtmlFragment(
        string fragment,
        UpstreamHtmlParseMode mode)
    {
        int fontSize = Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize);
        ConsoleContractLimits limits = HtmlContractLimits;
        try
        {
            UpstreamHtmlFragment parsed = HtmlManager.ParseFragment(fragment, new UpstreamHtmlParseOptions
            {
                Mode = mode,
                Budget = new UpstreamHtmlParseBudget(
                    limits.MaxHtmlInputLength,
                    limits.MaxHtmlTagCount,
                    limits.MaxHtmlNestingDepth,
                    limits.MaxHtmlSegmentCount,
                    limits.MaxHtmlPartCount,
                    limits.MaxHtmlTextLength)
            });
            return UpstreamHtmlTranslator.Translate(parsed, new UpstreamHtmlTranslationContext(
                limits,
                fontSize,
                Math.Max(fontSize, MinorShift.Emuera.Runtime.Config.Config.LineHeight),
                ToConsoleColor(MinorShift.Emuera.Runtime.Config.Config.ForeColor),
                ToConsoleColor(MinorShift.Emuera.Runtime.Config.Config.FocusColor),
                generation,
                imageResolver,
                mode));
        }
        catch (UpstreamHtmlBudgetExceededException exception)
        {
            throw new NotSupportedException(exception.ReasonCode, exception);
        }
        catch (UpstreamHtmlTranslationException exception)
        {
            throw new NotSupportedException(exception.ReasonCode, exception);
        }
        catch (ConsoleContractException exception)
        {
            throw new NotSupportedException(MapHtmlContractFailure(exception.Reason), exception);
        }
        catch (OverflowException exception)
        {
            throw new NotSupportedException("EMUERA_HTML_OUTPUT_LIMIT", exception);
        }
    }

    public void PrintHtml(string fragment, bool toPrintBuffer)
    {
        if (string.IsNullOrEmpty(fragment) || !Enabled)
            return;

        UpstreamHtmlParseMode mode = toPrintBuffer
            ? UpstreamHtmlParseMode.PrintBufferParts
            : UpstreamHtmlParseMode.DisplayLines;
        UpstreamHtmlTranslationResult translated = TranslateHtmlFragment(fragment, mode);
        if (!toPrintBuffer)
        {
            FlushPendingLine();
            // The desktop console flushes the ordinary PrintStringBuffer and
            // then appends HTML as a new display-line range. A partial PRINT
            // line must therefore not absorb the first HTML image/text node.
            lastLineCanAppend = false;
        }
        AppendHtmlNodes(translated.Nodes, translated.Alignment, translated.NoWrap, toPrintBuffer);
        if (!toPrintBuffer)
            FlushPendingLine();
    }

    public void PrintImg(string name, string nameb, string namem, MixedNum height, MixedNum width, MixedNum ypos)
    {
        RuntimeSpriteDefinition resolved = imageResolver?.Invoke(name);
        if (resolved is null)
            throw new NotSupportedException($"Sprite '{name}' is unavailable in the headless runtime.");
        RuntimeSpriteDefinition hover = string.IsNullOrEmpty(nameb) ? null : imageResolver?.Invoke(nameb);
        RuntimeSpriteDefinition mapping = string.IsNullOrEmpty(namem) ? null : imageResolver?.Invoke(namem);
        int fontSize = Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize);
        int targetHeight = height is null || height.num == 0
            ? fontSize
            : height.isPx ? height.num : checked(fontSize * height.num / 100);
        int targetWidth = width is null || width.num == 0
            ? checked(resolved.DestinationWidth * targetHeight / resolved.DestinationHeight)
            : width.isPx ? width.num : checked(fontSize * width.num / 100);
        int y = ypos is null ? 0 : ypos.isPx ? ypos.num : checked(fontSize * ypos.num / 100);
        AppendNode(CreateSpriteNode(name, resolved, targetWidth, targetHeight, y, hover, mapping));
    }

    private static SpriteNode CreateSpriteNode(
        string name,
        RuntimeSpriteDefinition resolved,
        int targetWidth,
        int targetHeight,
        int y,
        RuntimeSpriteDefinition hover,
        RuntimeSpriteDefinition mapping)
    {
        if (targetWidth == 0 || targetHeight == 0)
            throw new NotSupportedException("Sprite destination dimensions cannot be zero.");

        int positiveWidth = Math.Abs(targetWidth);
        int positiveHeight = Math.Abs(targetHeight);
        int destinationX = targetWidth < 0 ? positiveWidth : 0;
        int destinationY = y + (targetHeight < 0 ? positiveHeight : 0);
        destinationX = checked(destinationX + resolved.DestinationOffsetX * positiveWidth / resolved.DestinationWidth);
        destinationY = checked(destinationY + resolved.DestinationOffsetY * positiveHeight / resolved.DestinationHeight);
        return new SpriteNode(
            new ConsoleAssetId(resolved.AssetId),
            new ConsoleRect(resolved.SourceX, resolved.SourceY, resolved.SourceWidth, resolved.SourceHeight),
            new ConsoleRect(destinationX, destinationY, positiveWidth, positiveHeight),
            altText: name,
            hoverAssetId: hover is null ? null : new ConsoleAssetId(hover.AssetId),
            hoverSourceRect: hover is null ? null : new ConsoleRect(hover.SourceX, hover.SourceY, hover.SourceWidth, hover.SourceHeight),
            mappingAssetId: mapping is null ? null : new ConsoleAssetId(mapping.AssetId),
            mappingSourceRect: mapping is null ? null : new ConsoleRect(mapping.SourceX, mapping.SourceY, mapping.SourceWidth, mapping.SourceHeight),
            animationFrames: (resolved.AnimationFrames ?? Array.Empty<RuntimeSpriteFrame>()).Select(frame => new SpriteAnimationFrame(
                new ConsoleAssetId(frame.AssetId),
                new ConsoleRect(frame.SourceX, frame.SourceY, frame.SourceWidth, frame.SourceHeight),
                new ConsolePoint(frame.OffsetX, frame.OffsetY),
                frame.DurationMilliseconds)));
    }

    public void WaitInput(InputRequest request)
    {
        isTimeOut = false;
        FlushPendingLine();
        ConsoleInputType type = MapInputType(request.InputType);
        string defaultValue = request.HasDefValue
            ? type is ConsoleInputType.Integer or ConsoleInputType.IntegerButton
                ? request.DefIntValue.ToString(CultureInfo.InvariantCulture)
                : request.DefStrValue
            : null;
        TimeSpan? timeout = request.Timelimit > 0 ? TimeSpan.FromMilliseconds(request.Timelimit) : null;
        ConsolePromptTimeoutAction timeoutAction = request.HasDefValue
            ? ConsolePromptTimeoutAction.ReturnDefaultValue
            : ConsolePromptTimeoutAction.ContinueWithoutValue;
        var prompt = new ConsolePrompt(
            type,
            defaultValue: defaultValue,
            timeout: timeout,
            timeoutAction: timeoutAction,
            oneInput: request.OneInput,
            systemInput: request.IsSystemInput,
            stopMessageSkip: request.StopMesskip,
            displayTime: request.DisplayTime,
            timeoutMessage: request.TimeUpMes,
            allowedSources: ConsoleInputSource.All);
        if (request.DisplayTime && timeout is not null)
            EmitTimeoutCountdown(timeout.Value);
        GameConsoleInput input = adapter.Read(prompt, cancellationToken);
        isTimeOut = adapter is StructuredGameConsole structured && structured.IsTimeOut;
        if (isTimeOut && request.TimeUpMes is not null)
        {
            if (request.DisplayTime && lastLineId is not null)
            {
                EmitStructured(ConsoleOperation.ReplaceLine(new ConsoleLine(
                    lastLineId,
                    [new TextNode(request.TimeUpMes)],
                    ConsoleLineAlignment.Left,
                    temporary: false)));
                lastLineTemporary = false;
            }
            else
            {
                EmitLine(request.TimeUpMes, temporary: false);
            }
        }

        if (type is ConsoleInputType.Integer or ConsoleInputType.IntegerButton)
        {
            if (input.Value.Length == 0)
                return;
            long value = long.Parse(input.Value, CultureInfo.InvariantCulture);
            if (request.IsSystemInput)
                GlobalStatic.Process.InputSystemInteger(value);
            else
                GlobalStatic.Process.InputInteger(value);
        }
        else if (type is ConsoleInputType.Text or ConsoleInputType.TextButton)
        {
            if (request.IsSystemInput)
                GlobalStatic.Process.InputSystemInteger(request.HasDefValue ? request.DefIntValue : 0);
            GlobalStatic.Process.InputString(input.Value);
        }
        else if (type == ConsoleInputType.AnyValue)
        {
            if (long.TryParse(input.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
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
    }

    public void ReadAnyKey(bool anykey = false, bool stopMesskip = false)
    {
        isTimeOut = false;
        FlushPendingLine();
        adapter.Read(new ConsolePrompt(
            anykey ? ConsoleInputType.AnyKey : ConsoleInputType.EnterKey,
            stopMessageSkip: stopMesskip,
            allowedSources: ConsoleInputSource.All), cancellationToken);
    }

    public void Quit() => isRunning = false;
    public void ForceQuit() => Quit();
    public void ThrowError(bool playSound)
    {
        hasFatalError = true;
        Quit();
    }
    public void ThrowTitleError(bool error)
    {
        hasFatalError = true;
        Quit();
    }
    public void Await(int milliseconds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (milliseconds > 0)
            clock.DelayAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).AsTask().GetAwaiter().GetResult();
    }
    public void ResetStyle() => stringStyle = new StringStyle(Config.ForeColor, FontStyle.Regular, string.Empty);
    public void SetStringStyle(FontStyle style) => stringStyle.FontStyle = style;
    public void SetStringStyle(Color color)
    {
        stringStyle.Color = color;
        stringStyle.ColorChanged = color != Config.ForeColor;
    }
    public void SetFont(string fontName) => stringStyle.Fontname = fontName;
    public void SetBgColor(Color color) => bgColor = color;
    public void SetWindowTitle(string value)
    {
        windowTitle = value ?? string.Empty;
        if (outputEnabled)
            EmitStructured(ConsoleOperation.SetWindow(new WindowMetadata(windowTitle, viewportWidth, viewportHeight)));
    }
    public string GetWindowTitle() => windowTitle;
    public void UpdateGeneration() => generation++;
    public void forceUpdateGeneration() => generation++;
    public bool ButtonIsSelected(ConsoleButtonString button) => false;
    public bool ButtonIsPointing(ConsoleButtonString button) => false;
    public Point GetMousePosition() => Point.Empty;
    public bool MoveMouse(Point point) => false;

    public ConsoleDisplayLine[] GetDisplayLines(long lineNo) => DisplayLineList.ToArray();
    public ConsoleDisplayLine[] PopDisplayingLines()
    {
        ConsoleDisplayLine[] result = DisplayLineList.ToArray();
        DisplayLineList.Clear();
        return result;
    }
    public int GetLinePointY(int lineNo) => checked(lineNo * 16);
    public string getDefStBar() => barString;
    public string getStBar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Upstream StringMeasure.GetDisplayLength measures the real fixed-pitch
        // font; the headless runtime has no GDI text meter, so the same
        // DrawableWidth loop is reproduced with a deterministic em-based
        // estimate (wide/fullwidth glyph = one em, everything else = half em).
        int fontSize = Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize);
        int target = Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.DrawableWidth > 0
            ? MinorShift.Emuera.Runtime.Config.Config.DrawableWidth
            : viewportWidth);
        var builder = new System.Text.StringBuilder();
        int width = 0;
        while (width < target)
        {
            builder.Append(value);
            width = BarDisplayWidth(builder, fontSize);
        }
        while (width > target && builder.Length > 0)
        {
            builder.Length--;
            width = BarDisplayWidth(builder, fontSize);
        }
        return builder.ToString();
    }
    public void setStBar(string value) => barString = getStBar(value);
    public void PrintBar() => EmitBar(string.IsNullOrEmpty(barString)
        ? getStBar(MinorShift.Emuera.Runtime.Config.Config.DrawLineString)
        : barString);
    public void printCustomBar(string value, bool isConst)
    {
        if (string.IsNullOrEmpty(value))
            throw new MinorShift.Emuera.Runtime.Utils.CodeEE(
                MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error.EmptyDrawline.Text);
        EmitBar(isConst ? value : getStBar(value));
    }
    public bool OutputLog(string filename, bool hideInfo) => throw new NotSupportedException("The host log file capability is blocked in the headless runtime.");
    public bool OutputSystemLog(string filename) => throw new NotSupportedException("The host log file capability is blocked in the headless runtime.");
    public void OutputLog(string filename) => throw new NotSupportedException("The host log file capability is blocked in the headless runtime.");
    public void DebugPrint(string value) => RecordDebugMessage(value);
    public void DebugClear() => runtimeDebugMessages.Clear();
    public void DebugNewLine() => RecordDebugMessage(string.Empty);
    public void DebugAddTraceLog(string value) => RecordDebugMessage(value);
    public void DebugRemoveTraceLog() { }
    public void DebugClearTraceLog() => runtimeDebugMessages.Clear();

    public void PrintShape(params object[] args)
    {
        if (args.Length < 1 || args[0] is not string shapeName)
            throw new NotSupportedException("A structured shape requires a shape name.");
        MixedNum[] parameters = args.Length > 1 && args[1] is MixedNum[] mixed ? mixed : [];
        ConsoleShapeKind shape = shapeName.ToLowerInvariant() switch
        {
            "rect" => ConsoleShapeKind.Rectangle,
            "space" => ConsoleShapeKind.Space,
            "line" => ConsoleShapeKind.Line,
            "ellipse" => ConsoleShapeKind.Ellipse,
            _ => throw new NotSupportedException($"The shape '{shapeName}' is not in the structured allowlist.")
        };
        ConsoleRect bounds = shape == ConsoleShapeKind.Space && parameters.Length == 1
            ? new ConsoleRect(0, 0, Math.Max(1, MixedNum.ToPixel(parameters[0])), 16)
            : parameters.Length == 4
                ? new ConsoleRect(
                    MixedNum.ToPixel(parameters[0]),
                    MixedNum.ToPixel(parameters[1]),
                    MixedNum.ToPixel(parameters[2]),
                    MixedNum.ToPixel(parameters[3]))
                : new ConsoleRect(0, 0, Math.Max(1, MixedNum.ToPixel(parameters.FirstOrDefault())), 16);
        AppendNode(new ShapeNode(
            shape,
            bounds,
            fill: ToConsoleColor(stringStyle.Color),
            buttonColor: ToConsoleColor(stringStyle.ButtonColor)));
    }

    public void PrintHTMLIsland(params object[] args)
    {
        if (args.Length < 1 || args[0] is not string fragment)
            throw new NotSupportedException("An HTML Island requires a fragment.");
        UpstreamHtmlTranslationResult translated = TranslateHtmlFragment(
            fragment,
            UpstreamHtmlParseMode.DisplayLines);
        FlushPendingLine();
        htmlIslandDrawableId ??= "html-island";
        EmitStructured(ConsoleOperation.UpsertDrawable(new HtmlIslandDrawable(
            htmlIslandDrawableId,
            translated.Nodes,
            new ConsoleRect(0, 0, 1, 1),
            zIndex: 1)));
    }

    public void ClearHTMLIsland()
    {
        if (htmlIslandDrawableId is not null)
        {
            EmitStructured(ConsoleOperation.RemoveDrawable(htmlIslandDrawableId));
            htmlIslandDrawableId = null;
        }
    }

    public void AddBackgroundImage(params object[] args)
    {
        if (args.Length < 1 || args[0] is not string name)
            throw new NotSupportedException("A background requires a manifest sprite name.");
        RuntimeSpriteDefinition resolved = imageResolver?.Invoke(name);
        if (resolved is null)
            throw new NotSupportedException($"Background '{name}' is unavailable in the headless runtime.");
        long depth = args.Length > 1 ? Convert.ToInt64(args[1], CultureInfo.InvariantCulture) : 0;
        float opacity = args.Length > 2 ? Convert.ToSingle(args[2], CultureInfo.InvariantCulture) : 1f;
        EmitStructured(ConsoleOperation.UpsertBackground(new BackgroundLayer(
            name,
            new ConsoleAssetId(resolved.AssetId),
            opacity: opacity,
            depth: depth)));
    }

    public void ClearBackgroundImage() => EmitStructured(ConsoleOperation.ClearBackgrounds());

    public void RemoveBackground(params object[] args)
    {
        if (args.Length == 0 || args[0] is not string name)
            throw new NotSupportedException("A background id is required.");
        EmitStructured(ConsoleOperation.RemoveBackground(name));
    }

    public void CBG_Clear() => EmitStructured(ConsoleOperation.ClearScene());
    public void CBG_ClearRange(params object[] args)
    {
        if (args.Length < 2)
            throw new NotSupportedException("CBG_CLEAR range requires two bounds.");
        EmitStructured(ConsoleOperation.ClearSceneRange(
            Convert.ToInt32(args[0], CultureInfo.InvariantCulture),
            Convert.ToInt32(args[1], CultureInfo.InvariantCulture)));
    }
    public void CBG_ClearButton() => EmitStructured(ConsoleOperation.ClearHitRegions());
    public void CBG_ClearBMap() => EmitStructured(ConsoleOperation.ClearHitRegions());
    public bool CBG_SetGraphics(GraphicsImage graphics, int x, int y, int zdepth)
    {
        if (graphics is null || !graphics.IsCreated || graphics.Width <= 0 || graphics.Height <= 0 || zdepth == 0)
            return false;
        EmitRasterDrawable(EncodePng(graphics.Bitmap), null, x, y, graphics.Width, graphics.Height, zdepth, hitTestMap: false);
        return true;
    }

    public bool CBG_SetImage(ASprite image, int x, int y, int zdepth)
    {
        if (image is null || !image.IsCreated || zdepth == 0)
            return false;
        RuntimeSpriteDefinition resolved = imageResolver?.Invoke(image.Name);
        string id = NextCanvasId("image");
        if (resolved is not null)
        {
            EmitStructured(ConsoleOperation.UpsertDrawable(new SpriteDrawable(
                id,
                new ConsoleAssetId(resolved.AssetId),
                new ConsoleRect(resolved.SourceX, resolved.SourceY, resolved.SourceWidth, resolved.SourceHeight),
                new ConsoleRect(x + resolved.DestinationOffsetX, y + resolved.DestinationOffsetY,
                    resolved.DestinationWidth, resolved.DestinationHeight),
                zdepth,
                animationFrames: (resolved.AnimationFrames ?? Array.Empty<RuntimeSpriteFrame>()).Select(frame =>
                    new SpriteAnimationFrame(
                        new ConsoleAssetId(frame.AssetId),
                        new ConsoleRect(frame.SourceX, frame.SourceY, frame.SourceWidth, frame.SourceHeight),
                        new ConsolePoint(frame.OffsetX, frame.OffsetY),
                        frame.DurationMilliseconds)))));
            return true;
        }
        EmitRasterDrawable(RenderSprite(image), null, x, y, image.DestBaseSize.Width, image.DestBaseSize.Height, zdepth, false, id);
        return true;
    }

    public bool CBG_SetButtonMap(GraphicsImage graphics)
    {
        if (graphics is null || !graphics.IsCreated || graphics.Width <= 0 || graphics.Height <= 0)
            return false;
        EmitRasterDrawable(EncodePng(graphics.Bitmap), null, 0, 0, graphics.Width, graphics.Height, 0, hitTestMap: true, "cbg-hit-map");
        return true;
    }

    public bool CBG_SetButtonImage(int buttonValue, ASprite normal, ASprite hover, int x, int y, int zdepth, string tooltip = null)
    {
        if (normal is null || !normal.IsCreated || zdepth == 0)
            return false;
        string id = NextCanvasId("button");
        EmitRasterDrawable(
            RenderSprite(normal),
            hover is null || !hover.IsCreated ? null : RenderSprite(hover),
            x,
            y,
            normal.DestBaseSize.Width,
            normal.DestBaseSize.Height,
            zdepth,
            false,
            id);
        EmitStructured(ConsoleOperation.UpsertHitRegion(new HitRegion(
            id,
            new ConsoleRect(x, y, normal.DestBaseSize.Width, normal.DestBaseSize.Height),
            buttonValue.ToString(CultureInfo.InvariantCulture),
            tooltip: tooltip)));
        return true;
    }
    public void SetRedraw(params object[] args) => redrawIntervalMilliseconds = args.Length == 0 ? 0 : Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
    public void setRedrawTimer(params object[] args) => redrawIntervalMilliseconds = args.Length == 0 ? 0 : Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
    public void ReloadErbFinished() { }
    public void CustomToolTip(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipColor(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipDelay(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipDuration(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipFontName(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipFontSize(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipFormat(params object[] args) => throw HostTooltipBlocked();
    public void SetToolTipImg(params object[] args) => throw HostTooltipBlocked();

    private void AppendText(string value)
    {
        if (outputEnabled && !string.IsNullOrEmpty(value))
            pendingLine.Add(new TextNode(value, ToConsoleTextStyle()));
    }

    private void EmitText(string value) => AppendText(value);

    private void EmitButton(string label, string input)
    {
        if (outputEnabled && !string.IsNullOrEmpty(label))
            AppendNode(new ButtonNode(
                [new TextNode(label, ToConsoleTextStyle())],
                input,
                generation: generation));
    }

    private void EmitLine(string value) => EmitLine(value, temporary: false);

    private void EmitLine(string value, bool temporary)
    {
        if (!outputEnabled)
            return;

        // PrintSingleLine and diagnostic output flush the ordinary PRINT
        // buffer first. If that buffer was marked as a partial line, the
        // structured store will merge the next line through AppendInline,
        // matching the desktop console's IsLineEnd behavior.
        FlushPendingLine();
        if (string.IsNullOrEmpty(value))
            FlushPendingLine(force: true, temporary: temporary);
        else
        {
            AppendText(value);
            pendingLineEnd = true;
            FlushPendingLine(force: true, temporary: temporary);
        }
    }

    private void EmitBar(string value)
    {
        StringStyle previous = stringStyle;
        stringStyle.FontStyle = FontStyle.Regular;
        pendingLineNoWrap = true;
        Print(value);
        stringStyle = previous;
    }

    private static int BarDisplayWidth(System.Text.StringBuilder builder, int fontSize)
    {
        int width = 0;
        for (int index = 0; index < builder.Length; index++)
            width += IsWideBarCharacter(builder[index]) ? fontSize : Math.Max(1, fontSize / 2);
        return width;
    }

    private static bool IsWideBarCharacter(char value)
    {
        int code = value;
        return code >= 0x1100 && (
            code <= 0x115F ||
            code == 0x2329 || code == 0x232A ||
            (code >= 0x2E80 && code <= 0xA4CF && code != 0x303F) ||
            (code >= 0xAC00 && code <= 0xD7A3) ||
            (code >= 0xF900 && code <= 0xFAFF) ||
            (code >= 0xFE10 && code <= 0xFE19) ||
            (code >= 0xFE30 && code <= 0xFE6F) ||
            (code >= 0xFF00 && code <= 0xFF60) ||
            (code >= 0xFFE0 && code <= 0xFFE6));
    }

    private void AppendNode(ConsoleNode node, ConsoleLineAlignment? alignment = null, bool? noWrap = null)
    {
        if (alignment is not null)
            pendingLineAlignment = alignment;
        if (noWrap is not null)
            pendingLineNoWrap = noWrap.Value;
        if (node is LineBreakNode)
        {
            pendingLineEnd = true;
            FlushPendingLine(force: true);
        }
        else
            pendingLine.Add(node);
    }

    private void AppendHtmlNodes(
        IReadOnlyList<ConsoleNode> nodes,
        ConsoleLineAlignment alignment,
        bool noWrap,
        bool toPrintBuffer)
    {
        foreach (ConsoleNode node in nodes)
        {
            if (toPrintBuffer && node is LineBreakNode)
            {
                pendingBufferedLines.Add(new PendingBufferedLine(
                    pendingLine.ToArray(),
                    pendingLineAlignment ?? alignment,
                    pendingLineNoWrap || noWrap,
                    LineEnd: true));
                pendingLine.Clear();
                pendingLineAlignment = null;
                pendingLineNoWrap = false;
                pendingLineEnd = true;
                continue;
            }

            AppendNode(node, alignment, noWrap);
        }
    }

    private void FlushPendingLine(
        bool force = false,
        bool temporary = false,
        ConsoleLineAlignment? alignment = null,
        bool? noWrap = null)
    {
        if (!outputEnabled)
            return;

        if (!force && pendingLine.Count == 0 && pendingBufferedLines.Count == 0)
            return;

        var lines = new List<PendingBufferedLine>(pendingBufferedLines);
        if (pendingLine.Count > 0 || force)
        {
            if (force && pendingLine.Count == 0 && pendingBufferedLines.Count == 0)
                pendingLine.Add(new TextNode(" ", ToConsoleTextStyle()));
            lines.Add(new PendingBufferedLine(
                pendingLine.ToArray(),
                alignment ?? pendingLineAlignment,
                noWrap ?? pendingLineNoWrap,
                pendingLineEnd));
        }

        foreach (PendingBufferedLine line in lines)
        {
            IReadOnlyList<ConsoleNode> projectedNodes = AutoButtonize(line.Nodes);
            if (lastLineCanAppend && lastLineId is not null && projectedNodes.Count > 0)
            {
                EmitStructured(ConsoleOperation.AppendInline(lastLineId, projectedNodes));
            }
            else
            {
                string id = $"emuera-line-{checked(++lineId):x}";
                EmitStructured(ConsoleOperation.AppendLine(new ConsoleLine(
                    id,
                    projectedNodes,
                    line.Alignment ?? ToAlignment(),
                    temporary,
                    line.NoWrap)));
                lastLineId = id;
                lastLineTemporary = temporary;
                logicalLineCount = checked(logicalLineCount + 1);
                DisplayLineList.Add(new ConsoleDisplayLine([], isLogical: true, temporary: temporary));
            }

            lastLineCanAppend = !line.LineEnd;
        }

        pendingBufferedLines.Clear();
        pendingLine.Clear();
        pendingLineAlignment = null;
        pendingLineNoWrap = false;
        pendingLineEnd = true;
    }

    /// <summary>
    /// Reproduces the pinned desktop console's implicit numeric-button parsing.
    /// Ordinary PRINT/PRINTL output such as "[0] Yes  [1] No" is split by the
    /// upstream ButtonStringCreator when a physical line is flushed. The
    /// headless console must perform the same projection instead of exposing
    /// the complete line as inert text.
    /// </summary>
    private IReadOnlyList<ConsoleNode> AutoButtonize(IReadOnlyList<ConsoleNode> nodes)
    {
        var projected = new List<ConsoleNode>(nodes.Count);
        int index = 0;
        while (index < nodes.Count)
        {
            if (nodes[index] is not TextNode)
            {
                projected.Add(nodes[index++]);
                continue;
            }

            int start = index;
            while (index < nodes.Count && nodes[index] is TextNode)
                index++;
            AppendAutoButtonizedText(nodes.Skip(start).Take(index - start).Cast<TextNode>().ToArray(), projected);
        }

        return projected;
    }

    private void AppendAutoButtonizedText(IReadOnlyList<TextNode> textNodes, List<ConsoleNode> destination)
    {
        string text = string.Concat(textNodes.Select(node => node.Text));
        List<ButtonPrimitive> primitives = ButtonStringCreator.SplitButton(text);
        int offset = 0;
        foreach (ButtonPrimitive primitive in primitives)
        {
            TextNode[] children = SliceStyledText(textNodes, offset, primitive.Str.Length);
            if (primitive.CanSelect)
            {
                destination.Add(new ButtonNode(
                    children,
                    primitive.Input.ToString(CultureInfo.InvariantCulture),
                    generation: generation));
            }
            else
            {
                destination.AddRange(children);
            }
            offset = checked(offset + primitive.Str.Length);
        }
    }

    private static string FormatPrintCValue(string value, bool alignmentRight)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        int printCWidth = MinorShift.Emuera.Runtime.Config.Config.PrintCLength;
        if (printCWidth <= 0)
            return value;

        // Upstream uses the default replacement fallback for PRINTC width
        // measurement. The shared EncodingHandler instance is intentionally
        // strict for script/file decoding and must not be used here.
        int byteLength = printCByteCountEncoding.GetByteCount(value);
        if (alignmentRight && byteLength < printCWidth)
            return new string(' ', printCWidth - byteLength) + value;
        if (!alignmentRight && byteLength < printCWidth + 1)
            return value + new string(' ', printCWidth + 1 - byteLength);
        return value;
    }

    private static Encoding CreatePrintCByteCountEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("Shift-JIS");
    }

    private static TextNode[] SliceStyledText(IReadOnlyList<TextNode> nodes, int start, int length)
    {
        var result = new List<TextNode>();
        int end = checked(start + length);
        int nodeStart = 0;
        foreach (TextNode node in nodes)
        {
            int nodeEnd = checked(nodeStart + node.Text.Length);
            int sliceStart = Math.Max(start, nodeStart);
            int sliceEnd = Math.Min(end, nodeEnd);
            if (sliceStart < sliceEnd)
            {
                result.Add(new TextNode(
                    node.Text.Substring(sliceStart - nodeStart, sliceEnd - sliceStart),
                    node.Style));
            }
            nodeStart = nodeEnd;
            if (nodeStart >= end)
                break;
        }

        if (result.Count == 0)
            throw new InvalidDataException("The upstream button parser returned an empty display segment.");
        return result.ToArray();
    }

    private void EmitStructured(ConsoleOperation operation)
    {
        if (adapter is StructuredGameConsole structured)
            structured.EmitTransaction(new ConsoleTransaction([operation]));
        else
            adapter.Emit(operation);
    }

    private void EmitRasterDrawable(
        byte[] pngData,
        byte[] hoverPngData,
        int x,
        int y,
        int width,
        int height,
        int zdepth,
        bool hitTestMap,
        string drawableId = null)
    {
        EmitStructured(ConsoleOperation.UpsertDrawable(new RasterDrawable(
            drawableId ?? NextCanvasId("graphics"),
            pngData,
            new ConsoleRect(x, y, width, height),
            zdepth,
            hitTestMap ? 0f : 1f,
            hoverPngData,
            hitTestMap)));
    }

    private string NextCanvasId(string kind) => $"cbg-{kind}-{checked(++canvasDrawableId):x}";

    private static byte[] RenderSprite(ASprite sprite)
    {
        int width = sprite.DestBaseSize.Width;
        int height = sprite.DestBaseSize.Height;
        if (width <= 0 || height <= 0 || checked((long)width * height) > 16_777_216)
            throw new NotSupportedException("Dynamic Sprite dimensions exceed the bounded raster surface.");
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            sprite.GraphicsDraw(graphics, new Rectangle(0, 0, width, height));
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private void EmitTimeoutCountdown(TimeSpan timeout) =>
        EmitLine($"Time remaining: {timeout.TotalSeconds:0.0}", temporary: true);

    private ConsoleTextStyle ToConsoleTextStyle()
    {
        ConsoleFontStyle decorations = ConsoleFontStyle.None;
        if ((stringStyle.FontStyle & FontStyle.Bold) != 0) decorations |= ConsoleFontStyle.Bold;
        if ((stringStyle.FontStyle & FontStyle.Italic) != 0) decorations |= ConsoleFontStyle.Italic;
        if ((stringStyle.FontStyle & FontStyle.Underline) != 0) decorations |= ConsoleFontStyle.Underline;
        if ((stringStyle.FontStyle & FontStyle.Strikeout) != 0) decorations |= ConsoleFontStyle.Strike;
        return new ConsoleTextStyle(
            ToConsoleColor(stringStyle.Color),
            ToConsoleColor(bgColor),
            decorations,
            string.IsNullOrWhiteSpace(stringStyle.Fontname) ? "default" : stringStyle.Fontname,
            buttonColor: ToConsoleColor(stringStyle.ButtonColor));
    }

    private ConsoleContractLimits HtmlContractLimits => adapter is StructuredGameConsole structured
        ? structured.StateStore.Options.ContractLimits
        : ConsoleContractLimits.Default;

    private static string MapHtmlContractFailure(ConsoleContractViolationReason reason) => reason switch
    {
        ConsoleContractViolationReason.TextTooLong or
        ConsoleContractViolationReason.TooltipTooLong or
        ConsoleContractViolationReason.ButtonValueTooLong or
        ConsoleContractViolationReason.AltTextTooLong or
        ConsoleContractViolationReason.BatchTooLarge or
        ConsoleContractViolationReason.TooManyButtonLabelNodes or
        ConsoleContractViolationReason.NodeTooDeep or
        ConsoleContractViolationReason.InvalidGeometry or
        ConsoleContractViolationReason.InvalidImageDimension or
        ConsoleContractViolationReason.ImageTooLarge or
        ConsoleContractViolationReason.GeometryTooLarge or
        ConsoleContractViolationReason.InvalidSpriteFrame or
        ConsoleContractViolationReason.HtmlNodeLimitExceeded => "EMUERA_HTML_OUTPUT_LIMIT",
        _ => "EMUERA_HTML_TRANSLATION_UNSUPPORTED"
    };

    private static CloudEmuera.RuntimeAdapter.ConsoleColor? ToConsoleColor(Color color) => color.IsEmpty
        ? null
        : CloudEmuera.RuntimeAdapter.ConsoleColor.FromRgba(color.R, color.G, color.B, color.A);

    private ConsoleLineAlignment ToAlignment() => Alignment switch
    {
        DisplayLineAlignment.CENTER => ConsoleLineAlignment.Center,
        DisplayLineAlignment.RIGHT => ConsoleLineAlignment.Right,
        _ => ConsoleLineAlignment.Left
    };

    private static ConsoleInputType MapInputType(InputType inputType) => inputType switch
    {
        InputType.EnterKey => ConsoleInputType.EnterKey,
        InputType.AnyKey => ConsoleInputType.AnyKey,
        InputType.IntValue => ConsoleInputType.Integer,
        InputType.StrValue => ConsoleInputType.Text,
        InputType.Void => ConsoleInputType.WaitOnly,
        InputType.AnyValue => ConsoleInputType.AnyValue,
        InputType.IntButton => ConsoleInputType.IntegerButton,
        InputType.StrButton => ConsoleInputType.TextButton,
        InputType.PrimitiveMouseKey => ConsoleInputType.PrimitivePointerKey,
        _ => throw new NotSupportedException($"The upstream input type '{inputType}' is unavailable.")
    };

    private void RecordMessage(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            runtimeMessages.Add(value.Trim());
    }

    private void RecordMessageAndDisplay(string value)
    {
        RecordMessage(value);
        EmitDiagnosticLine(value);
    }

    private void RecordWarning(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            runtimeWarnings.Add(value.Trim());
    }

    private void EmitDiagnosticLine(string value)
    {
        if (outputEnabled && !string.IsNullOrWhiteSpace(value))
            EmitLine($"⚠ {value.Trim()}");
    }

    private void RecordSystemMessage(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            runtimeSystemMessages.Add(value.Trim());
    }

    private void RecordDebugMessage(string value)
    {
        if (runtimeDebugMessages.Count >= 256)
            runtimeDebugMessages.RemoveAt(0);
        runtimeDebugMessages.Add(value.Length > 4_096 ? value[..4_096] : value);
    }

    private static string FormatDiagnostic(string value, ScriptPosition? position) =>
        position is { } located
            ? $"{value} ({located.Filename}:{located.LineNo})"
            : value;

    private static NotSupportedException HostTooltipBlocked() =>
        new("HOST_SHIM: desktop custom tooltip capabilities are blocked in the headless runtime.");
}
