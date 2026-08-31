// CloudEmuera modification: Linux headless Console implementation for the
// pinned upstream parser/process. No desktop control or message pump is used.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using MinorShift.Emuera.UI;
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
    private readonly string fontFaceId;
    private readonly string fontCatalogDigest;
    private readonly string webFontAssetDigest;
    private readonly bool convertBackslashToYen;
    private readonly StringMeasure stringMeasure;
    private readonly PrintStringBuffer printBuffer;
    private string barString = "-";
    private bool isRunning = true;
    private bool hasFatalError;
    private bool isTimeOut;
    // The pinned desktop console exposes mouse coordinates in a client
    // coordinate system whose origin is at the lower-left: its GetMousePosition
    // subtracts ClientHeight from the browser/control Y coordinate. Keep that
    // transformed position until the next pointer event so ERB helpers such
    // as MOUSEX()/MOUSEY() see the coordinates of the input that woke them.
    private Point mousePosition = Point.Empty;
    private string windowTitle = string.Empty;
    // One authoritative mirror of the pinned upstream button-generation
    // state machine. `nextButtonGeneration` stamps newly-created actions;
    // `activeButtonGeneration` is the generation eligible at the current
    // prompt and used by BINPUT's legacy display facade.
    private long nextButtonGeneration = 1;
    private long activeButtonGeneration = 1;
    private bool lastButtonInputWasInteger = true;
    private LogicalLine lastInputLine;
    private long lineId;
    private long logicalLineCount;
    private long deletedLines;
    private string? lastLineId;
    private string? lastPhysicalLineId;
    // CLEARLINE 1 is commonly followed immediately by a reprint for
    // animation. Keep the physical line identity until that replacement is
    // available so the browser can update its existing Canvas nodes.
    private string? deferredReplacementLogicalLineId;
    private bool lastLineCanAppend;
    private bool lastLineTemporary;
    private string? htmlIslandDrawableId;
    private int redrawIntervalMilliseconds;
    private long canvasDrawableId;
    private readonly List<ConsoleNode> pendingLine = [];
    private readonly List<PendingBufferedLine> pendingBufferedLines = [];
    // The structured ButtonNode is the browser-facing representation. Keep a
    // small identity set for integer buttons so the pinned BINPUT code can
    // receive the same typed button through DisplayLineList without exposing
    // upstream UI types in RuntimeAdapter.
    private readonly HashSet<ButtonNode> integerButtonNodes = [];
    // BINPUT* is implemented by the pinned interpreter and reads the legacy
    // display-line facade. Keep a source-node projection beside that facade so
    // appends, CLEARLINE replacements, and deletions cannot leave stale button
    // inventory behind when the structured browser output is updated.
    private readonly List<IReadOnlyList<ConsoleNode>> legacyDisplayLineNodes = [];
    private ConsoleLineAlignment? pendingLineAlignment;
    private bool pendingLineNoWrap;
    private bool pendingLineEnd = true;
    private StringStyle stringStyle;
    private readonly List<string> runtimeMessages = [];
    private readonly List<string> runtimeWarnings = [];
    private readonly HashSet<string> ignoredGameFonts = new(StringComparer.Ordinal);
    private readonly List<string> runtimeSystemMessages = [];
    private readonly List<string> runtimeDebugMessages = [];
    private readonly List<string> pendingDiagnosticLines = [];
    // Issue #2: retain the desktop right-click message-skip mode while the
    // interpreter advances through consecutive Enter/AnyKey waits. A
    // separate flag prevents script-level SKIPLOG state from being cleared by
    // an unrelated browser input.
    private bool inputMessageSkipActive;
    private bool outputEnabled;
    private readonly Dictionary<int, long> dirtyTooltipGraphics = [];
    private readonly HashSet<string> tooltipProjectionWarnings = new(StringComparer.Ordinal);
    private bool tooltipProjectionActive;
    private bool tooltipProjectionImageMode;

    private sealed record PendingBufferedLine(
        IReadOnlyList<ConsoleNode> Nodes,
        ConsoleLineAlignment? Alignment,
        bool NoWrap,
        bool Truncate,
        bool LineEnd);

    private sealed record LayoutAtom(
        IReadOnlyList<ConsoleNode> Children,
        int Width,
        bool CanDivide,
        string? Text,
        ConsoleTextStyle? TextStyle,
        ConsoleInlineAction? Action,
        int? LockedX,
        bool LockedXIsRelative);

    private sealed record PhysicalLineDraft(
        IReadOnlyList<PositionedInlineSegmentNode> Segments,
        int ContentWidth);

    public EmueraConsole(
        IGameConsole adapter,
        IRuntimeClock clock,
        CancellationToken cancellationToken,
        Func<string, RuntimeSpriteDefinition> imageResolver = null,
        int viewportWidth = 800,
        int viewportHeight = 600,
        string fontFaceId = "sarasa-fixed-sc-1.0.40-regular", string fontCatalogDigest = "", string webFontAssetDigest = "",
        bool convertBackslashToYen = true)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.cancellationToken = cancellationToken;
        this.imageResolver = imageResolver;
        if (viewportWidth <= 0 || viewportHeight <= 0 || viewportWidth > 8_192 || viewportHeight > 8_192)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "The logical headless viewport is outside its limit.");
        this.viewportWidth = viewportWidth;
        this.viewportHeight = viewportHeight;
        this.fontFaceId = fontFaceId ?? string.Empty;
        this.fontCatalogDigest = fontCatalogDigest ?? string.Empty;
        this.webFontAssetDigest = webFontAssetDigest ?? string.Empty;
        this.convertBackslashToYen = convertBackslashToYen;
        // Emuera starts with the color loaded from emuera.config. Keep the
        // initial structured window metadata consistent with the upstream
        // desktop console before the game calls SETBGCOLOR.
        bgColor = Config.BackColor;
        stringStyle = new StringStyle(Config.ForeColor, FontStyle.Regular, string.Empty);
        // BINPUT/BINPUTS access the upstream print buffer even though the
        // headless structured path does not use it to render output. Keep the
        // compatibility object available in the font-less test/fallback path.
        printBuffer = new PrintStringBuffer(this);
        if (Config.DefaultFont is not null)
            stringMeasure = new StringMeasure();
        GlobalStatic.Console = this;
    }

    public bool IsRunning => isRunning;
    public bool HasFatalError => hasFatalError;
    public void SetCancellationToken(CancellationToken value) => cancellationToken = value;
    public void BeginExecutionOutput()
    {
        outputEnabled = true;
        if (adapter is StructuredGameConsole)
            EmitStructured(ConsoleOperation.SetWindow(CurrentWindowMetadata()));
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
    public PrintStringBuffer PrintBuffer => printBuffer;
    public StringMeasure StrMeasure => stringMeasure;
    public ConsoleButtonString SelectingButton => null;
    public ConsoleButtonString PointingSring => null;
    public ConsoleButtonString[] bitmapCacheArray = new ConsoleButtonString[256];
    public const nint bitmapCacheArrayCap = 256;
    public nint bitmapCacheArrayIndex;
    public long LastButtonGeneration => activeButtonGeneration;
    public long NewButtonGeneration => nextButtonGeneration;
    internal long LegacyNewButtonGeneration => nextButtonGeneration;
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
    // separately, while PrintError remains an output channel rather than a
    // fatality transition. Initialization's bool result and HasFatalError are
    // the authoritative error signals. Neither warnings nor recoverable
    // messages are written into the player's console transcript; fatal error
    // reporting flushes its diagnostics.
    public void PrintSystemLine(string value) => RecordSystemMessage(value);
    public void PrintError(string value)
    {
        RecordMessage(value);
        QueueDiagnosticLine(value);
    }
    public void PrintWarning(string value, ScriptPosition? position, int level) =>
        RecordWarning(FormatDiagnostic(value, position));
    public void PrintErrorButton(string value, ScriptPosition? position, int level = 0) =>
        RecordMessageAndQueue(FormatDiagnostic(value, position));
    public void PrintTemporaryLine(string value) => EmitLine(value, temporary: true);
    public void PrintPlain(string value) => EmitText(value);
    public void PrintPlainWithSingleLineFix(string value) => EmitLine(value);
    public void PrintC(string value, bool alignmentRight)
    {
        if (!outputEnabled || string.IsNullOrEmpty(value))
            return;
        // Upstream PRINTC appends a fixed-width field to PrintStringBuffer. It
        // does not commit a display line; PRINTL/PrintFlush owns that boundary.
        pendingLine.Add(new TextNode(DisplayText(FormatPrintCValue(value, alignmentRight)), ToConsoleTextStyle()));
        pendingLineEnd = true;
    }
    public void PrintButton(string value, string input) => EmitButton(value, input, isInteger: false);
    public void PrintButton(string value, long input) => EmitButton(value, input.ToString(CultureInfo.InvariantCulture), isInteger: true);
    public void PrintButtonC(string value, string input, bool isRight) => EmitPrintCButton(value, input, isRight, isInteger: false);
    public void PrintButtonC(string value, long input, bool isRight) => EmitPrintCButton(value, input.ToString(CultureInfo.InvariantCulture), isRight, isInteger: true);
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
    // BINPUT is the exception in the headless bridge: direct PRINTBUTTON calls
    // are held in pendingLine rather than the desktop PrintStringBuffer, so a
    // button-bearing pending line must be committed before the pinned
    // interpreter inspects DisplayLineList.
    public void RefreshStrings(bool forcePaint)
    {
        if (EnumerateLegacyButtons(pendingLine).Any() ||
            pendingBufferedLines.Any(line => EnumerateLegacyButtons(line.Nodes).Any()))
            FlushPendingLine();
        FlushDeferredReplacementDelete();
        ProjectTooltipResources();
    }
    public void ClearText()
    {
        FlushPendingLine();
        FlushDeferredReplacementDelete();
        EmitStructured(ConsoleOperation.ClearConsole());
        DisplayLineList.Clear();
        legacyDisplayLineNodes.Clear();
        integerButtonNodes.Clear();
        lastLineId = null;
        lastPhysicalLineId = null;
        lastLineCanAppend = false;
        lastLineTemporary = false;
    }
    public void ClearDisplay() => ClearText();
    public void deleteLine(int count)
    {
        if (count <= 0 || adapter is not StructuredGameConsole structured)
            return;
        // CLEARLINE counts upstream logical display lines. Font-authoritative
        // layout can represent one such line with several physical rows, so
        // deleting only the last physical id leaves whitespace-only wrapped
        // fragments in the browser scrollback.
        List<List<ConsoleLine>> logicalGroups = structured.Snapshot.Scrollback
            .GroupBy(line => line.LogicalLineId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(line => line.PhysicalIndex)
                .ThenBy(line => line.LineId, StringComparer.Ordinal)
                .ToList())
            .ToList();
        int groupCount = Math.Min(count, logicalGroups.Count);
        if (groupCount == 0)
            return;
        List<List<ConsoleLine>> groupsToDelete = logicalGroups.TakeLast(groupCount).ToList();
        string[] ids = groupsToDelete
            .SelectMany(group => group)
            .Select(line => line.LineId)
            .ToArray();
        if (count == 1 && groupsToDelete.Count == 1)
        {
            List<ConsoleLine> group = groupsToDelete[0];
            RemoveLegacyDisplayLines(groupsToDelete.Count);
            deferredReplacementLogicalLineId = group[0].LogicalLineId;
            lastLineId = group[^1].LineId;
            lastPhysicalLineId = group[^1].LineId;
            lastLineCanAppend = false;
            lastLineTemporary = group[^1].Temporary;
            return;
        }
        try
        {
            deferredReplacementLogicalLineId = null;
            EmitStructured(ConsoleOperation.DeleteLines(ids));
            RemoveLegacyDisplayLines(groupsToDelete.Count);
            deletedLines = checked(deletedLines + groupsToDelete.Count);
            ConsoleLine? remaining = structured.Snapshot.Scrollback.LastOrDefault();
            lastLineId = remaining?.LineId;
            lastPhysicalLineId = remaining?.LineId;
            lastLineCanAppend = false;
            lastLineTemporary = remaining?.Temporary ?? false;
            logicalLineCount = Math.Max(0, logicalLineCount - groupsToDelete.Count);
        }
        catch (ConsoleContractException)
        {
            // A line trimmed by the bounded scrollback is already absent.
        }
    }

    private static IReadOnlyList<ConsoleLine> GetLogicalLineGroup(
        StructuredGameConsole console,
        string logicalLineId) => console.Snapshot.Scrollback
            .Where(line => string.Equals(line.LogicalLineId, logicalLineId, StringComparison.Ordinal))
            .OrderBy(line => line.PhysicalIndex)
            .ThenBy(line => line.LineId, StringComparer.Ordinal)
            .ToArray();

    // CloudEmuera: AppContents is the pinned upstream registry for sprites
    // created during execution. Static resources arrive through imageResolver,
    // while a SpriteG created from a saved or in-memory GraphicsImage must
    // resolve to a SessionRoot PNG so HTML_PRINT can publish a browser asset.
    private RuntimeSpriteDefinition ResolveSpriteDefinition(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        RuntimeSpriteDefinition dynamic = TryResolveDynamicSprite(AppContents.GetSprite(name));
        return dynamic ?? imageResolver?.Invoke(name);
    }

    private RuntimeSpriteDefinition TryResolveDynamicSprite(ASprite sprite)
    {
        if (sprite is not SpriteG spriteG ||
            spriteG.BaseImage is not GraphicsImage graphics ||
            !spriteG.IsCreated)
            return null;

        Rectangle source = spriteG.SrcRectangle;
        if (source.Width <= 0 || source.Height <= 0 ||
            spriteG.DestBaseSize.Width <= 0 || spriteG.DestBaseSize.Height <= 0)
            return null;

        try
        {
            string assetPath = graphics.HeadlessAssetPath;
            if (string.IsNullOrWhiteSpace(assetPath) ||
                HeadlessPathResolver.ResolveExisting(Path.Combine(
                    MinorShift.Emuera.Program.ExeDir,
                    assetPath.Replace('/', Path.DirectorySeparatorChar))) is null)
            {
                assetPath = MaterializeDynamicGraphics(graphics);
            }

            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            return new RuntimeSpriteDefinition(
                ConsoleAssetIdCodec.EncodePath(assetPath),
                source.X,
                source.Y,
                source.Width,
                source.Height,
                spriteG.DestBasePosition.X,
                spriteG.DestBasePosition.Y,
                spriteG.DestBaseSize.Width,
                spriteG.DestBaseSize.Height);
        }
        catch (ArgumentException)
        {
            // The save bridge only stores controlled paths. If an invalid
            // value somehow reaches this seam, retain the normal image
            // fallback instead of failing the interpreter.
            return null;
        }
    }

    private static string MaterializeDynamicGraphics(GraphicsImage graphics)
    {
        try
        {
            // GCREATE/OVERLAY_GCREATE surfaces are mutable in-memory state and
            // have no native sav file until the game explicitly calls GSAVE.
            // Materialize the current pixels under SessionRoot on first use so
            // the browser can fetch the same surface through the asset gate.
            byte[] pngData = EncodePng(graphics.Bitmap);
            string digest = Convert.ToHexString(SHA256.HashData(pngData)).ToLowerInvariant();
            string fullPath = Path.Combine(
                MinorShift.Emuera.Program.ExeDir,
                "tmp",
                "cloudemuera-runtime-assets",
                $"{digest}.png");
            string createPath = HeadlessPathResolver.ForCreate(fullPath);
            string parent = Path.GetDirectoryName(createPath);
            if (string.IsNullOrWhiteSpace(parent))
                return null;

            Directory.CreateDirectory(parent);
            if (!File.Exists(createPath))
                File.WriteAllBytes(createPath, pngData);

            string resolved = HeadlessPathResolver.ResolveExisting(fullPath);
            string logicalPath = ToHeadlessLogicalPath(resolved);
            if (logicalPath is null)
                return null;

            graphics.HeadlessAssetPath = logicalPath;
            return logicalPath;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or ExternalException)
        {
            // A transient raster/file failure must retain the normal bounded
            // literal fallback. It must not make the interpreter fail merely
            // because a browser asset could not be published.
            return null;
        }
    }

    private static string ToHeadlessLogicalPath(string resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        string relative = Path.GetRelativePath(MinorShift.Emuera.Program.ExeDir, resolved);
        if (relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            return null;

        return relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private UpstreamHtmlTranslationResult TranslateHtmlFragment(
        string fragment,
        UpstreamHtmlParseMode mode,
        Action<ButtonNode> integerButtonMarker = null)
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
                nextButtonGeneration,
                ResolveSpriteDefinition,
                mode,
                convertBackslashToYen,
                integerButtonMarker));
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
        var integerButtons = new List<ButtonNode>();
        UpstreamHtmlTranslationResult translated = TranslateHtmlFragment(
            fragment,
            mode,
            integerButtonMarker: integerButtons.Add);
        // Do not mutate the legacy inventory while parsing/translating: a
        // failed HTML fragment must not leave behind button state that was
        // never submitted to the structured console.
        foreach (ButtonNode button in integerButtons)
            integerButtonNodes.Add(button);
        if (!toPrintBuffer)
        {
            FlushPendingLine();
            // The desktop console flushes the ordinary PrintStringBuffer and
            // then appends HTML as a new display-line range. A partial PRINT
            // line must therefore not absorb the first HTML image/text node.
            lastLineCanAppend = false;
        }
        AppendHtmlNodes(translated.Nodes, translated.Alignment, translated.NoWrap, toPrintBuffer);
        if (ContainsSelectableAction(translated.Nodes))
            UpdateGeneration();
        if (!toPrintBuffer)
            FlushPendingLine();
    }

    public void PrintImg(string name, string nameb, string namem, MixedNum height, MixedNum width, MixedNum ypos)
    {
        RuntimeSpriteDefinition resolved = ResolveSpriteDefinition(name);
        if (resolved is null)
            throw new NotSupportedException($"Sprite '{name}' is unavailable in the headless runtime.");
        RuntimeSpriteDefinition hover = string.IsNullOrEmpty(nameb) ? null : ResolveSpriteDefinition(nameb);
        RuntimeSpriteDefinition mapping = string.IsNullOrEmpty(namem) ? null : ResolveSpriteDefinition(namem);
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
        FlushDeferredReplacementDelete();
        ProjectTooltipResources();
        ConsoleInputType type = MapInputType(request.InputType);
        if (ShouldSkipMessageWait(type, request.StopMesskip))
            return;

        PrepareInputButtonGeneration(request.InputType);

        // Desktop PressEnterKey stops message skipping as soon as the next
        // input needs a value (or is a forced wait). Keep that boundary when
        // the headless adapter resumes the interpreter on its own thread.
        ClearInputMessageSkip();
        // TINPUT's default is a timeout/result value, not text prefilled in
        // the desktop input box. Keep it hidden while the timer is active and
        // apply it after the adapter reports a timeout below.
        bool timedInput = request.Timelimit > 0;
        string defaultValue = request.HasDefValue && !timedInput
            ? type is ConsoleInputType.Integer or ConsoleInputType.IntegerButton
                ? request.DefIntValue.ToString(CultureInfo.InvariantCulture)
                : request.DefStrValue
            : null;
        TimeSpan? timeout = request.Timelimit > 0 ? TimeSpan.FromMilliseconds(request.Timelimit) : null;
        ConsolePromptTimeoutAction timeoutAction = request.HasDefValue
            ? timedInput ? ConsolePromptTimeoutAction.ContinueWithoutValue : ConsolePromptTimeoutAction.ReturnDefaultValue
            : ConsolePromptTimeoutAction.ContinueWithoutValue;
        var prompt = new ConsolePrompt(
            type,
            defaultValue: defaultValue,
            // Emuera parses integer INPUT/INTBUTTON values with long.TryParse,
            // so both typed values and button values may carry a sign. Keep
            // the browser-side contract aligned with the upstream runtime;
            // otherwise clicking a valid negative button is rejected before
            // it can reach Emuera.
            constraints: type is ConsoleInputType.Integer or ConsoleInputType.IntegerButton
                ? new IntegerInputConstraints(allowSign: true)
                : null,
            timeout: timeout,
            timeoutBehavior: timedInput && request.HasDefValue
                ? ConsolePromptTimeoutBehavior.ContinueWithoutValue
                : request.HasDefValue ? ConsolePromptTimeoutBehavior.ReturnDefaultValue : ConsolePromptTimeoutBehavior.ContinueWithoutValue,
            timeoutAction: timeoutAction,
            oneInput: request.OneInput,
            systemInput: request.IsSystemInput,
            stopMessageSkip: request.StopMesskip,
            displayTime: request.DisplayTime,
            timeoutMessage: request.TimeUpMes is null ? null : DisplayText(request.TimeUpMes),
            // Pointer presses outside a game button are an INPUT/INPUTS
            // capability only when the optional mouse argument is enabled.
            // Button activation remains available for ordinary input, while
            // the pointer bit lets the browser distinguish INPUTS,1 from a
            // normal text prompt without adding a second protocol flag.
            allowedSources: ConsoleInputSource.Keyboard | ConsoleInputSource.Button |
                (request.MouseInput ? ConsoleInputSource.Pointer : ConsoleInputSource.None),
            allowLongInputByButton: Config.AllowLongInputByMouse,
            buttonGeneration: activeButtonGeneration);
        if (request.DisplayTime && timeout is not null)
            EmitTimeoutCountdown(timeout.Value);
        GameConsoleInput input = adapter.Read(prompt, cancellationToken);
        ApplyMouseInputResults(request, input);
        ApplyInputMessageSkip(input);
        // BINPUT validates its inventory before opening the prompt. Once that
        // prompt closes, retire the consumed button generation before the
        // interpreter can execute another BINPUT inventory scan.
        if (request.InputType is InputType.IntButton or InputType.StrButton)
            activeButtonGeneration = nextButtonGeneration;
        isTimeOut = adapter is StructuredGameConsole structured && structured.IsTimeOut;
        if (isTimeOut && request.TimeUpMes is not null)
        {
            if (request.DisplayTime && lastLineId is not null)
            {
                EmitStructured(ConsoleOperation.ReplaceLine(new ConsoleLine(
                    lastLineId,
                    [new TextNode(DisplayText(request.TimeUpMes))],
                    ConsoleLineAlignment.Left,
                    temporary: false)));
                lastLineTemporary = false;
            }
            else
            {
                EmitLine(request.TimeUpMes, temporary: false);
            }
        }

        if (isTimeOut && request.HasDefValue)
        {
            if (type is ConsoleInputType.Text or ConsoleInputType.TextButton)
                GlobalStatic.Process.InputString(request.DefStrValue ?? string.Empty);
            else if (request.IsSystemInput)
                GlobalStatic.Process.InputSystemInteger(request.DefIntValue);
            else
                GlobalStatic.Process.InputInteger(request.DefIntValue);
            EchoAcceptedInput(type is ConsoleInputType.Text or ConsoleInputType.TextButton
                ? request.DefStrValue ?? string.Empty
                : request.DefIntValue.ToString(CultureInfo.InvariantCulture));
            return;
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
            EchoAcceptedInput(input.Value);
        }
        else if (type is ConsoleInputType.Text or ConsoleInputType.TextButton)
        {
            if (request.IsSystemInput)
                GlobalStatic.Process.InputSystemInteger(request.HasDefValue ? request.DefIntValue : 0);
            GlobalStatic.Process.InputString(input.Value);
            EchoAcceptedInput(input.Value);
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
            EchoAcceptedInput(input.Value);
        }
    }

    public void ReadAnyKey(bool anykey = false, bool stopMesskip = false)
    {
        isTimeOut = false;
        FlushPendingLine();
        FlushDeferredReplacementDelete();
        ProjectTooltipResources();
        ConsoleInputType inputType = anykey ? ConsoleInputType.AnyKey : ConsoleInputType.EnterKey;
        // Match upstream EmueraConsole.ReadAnyKey: a wait emitted by
        // EVENTCOMEND suppresses Process' fallback post-command wait.
        if (GlobalStatic.Process is not null)
            GlobalStatic.Process.NeedWaitToEventComEnd = false;
        if (RuntimeDebugTrace.Current is not null)
            RuntimeDebugTrace.RecordErbWait(GlobalStatic.Process?.GetRunningPosition(), inputType, stopMesskip);
        if (ShouldSkipMessageWait(inputType, stopMesskip))
            return;

        ClearInputMessageSkip();
        GameConsoleInput input = adapter.Read(new ConsolePrompt(
            inputType,
            stopMessageSkip: stopMesskip,
            allowedSources: ConsoleInputSource.All,
            buttonGeneration: activeButtonGeneration), cancellationToken);
        ApplyInputMessageSkip(input);
        EchoAcceptedInput(input.Value);
    }

    /// <summary>
    /// Mirrors pinned desktop Emuera's successful input path: after the
    /// interpreter accepts the value, doInputToEmueraProgram calls Print and
    /// PrintFlush so the submitted value becomes ordinary console history.
    /// Keeping the echo in the headless Worker runtime makes it part of the
    /// sequenced display state and every committed snapshot. Empty values
    /// remain invisible through Print's existing upstream-compatible rule.
    /// </summary>
    private void EchoAcceptedInput(string value)
    {
        Print(value);
        FlushPendingLine();
    }

    private bool ShouldSkipMessageWait(ConsoleInputType inputType, bool stopMessageSkip) =>
        inputMessageSkipActive && MesSkip && !stopMessageSkip &&
        inputType is ConsoleInputType.EnterKey or ConsoleInputType.AnyKey;

    private void ApplyInputMessageSkip(GameConsoleInput input)
    {
        if (input.SkipMessage)
        {
            MesSkip = true;
            inputMessageSkipActive = true;
        }
    }

    /// <summary>
    /// Mirrors the pinned desktop mouse-input path for INPUT/INPUTS requests
    /// whose optional mouse argument is enabled. The browser pointer button
    /// uses DOM numbering (left=0, middle=1, right=2), while Emuera exposes
    /// left=1, right=2, middle=3 through RESULT:1. The selected button value
    /// is exposed through RESULTS:1 before the normal textual input path
    /// stores the value in RESULTS.
    /// </summary>
    private void ApplyMouseInputResults(InputRequest request, GameConsoleInput input)
    {
        if (input.Pointer is not { } pointer)
            return;

        // The pointer payload is in the runtime's top-left client coordinate
        // system, matching the coordinate received by the pinned desktop
        // MouseEventArgs path. The upstream MOUSEX/MOUSEY methods expose the
        // Y coordinate after converting it to the lower-left origin.
        mousePosition = new Point(
            pointer.Position.X,
            checked(pointer.Position.Y - viewportHeight));

        if (!request.MouseInput || !pointer.Pressed)
            return;

        int result = pointer.Button switch
        {
            0 => 1,
            1 => 3,
            2 => 2,
            _ => 0,
        };
        if (result == 0)
            return;

        GlobalStatic.VEvaluator.RESULTS_ARRAY[1] = input.Value;
        GlobalStatic.VEvaluator.RESULT_ARRAY[1] = result;
        // Modifier and mapped-colour payloads are not part of the current
        // structured pointer contract. Clear these slots rather than leaking
        // values left by an earlier upstream input event.
        GlobalStatic.VEvaluator.RESULT_ARRAY[2] = 0;
        GlobalStatic.VEvaluator.RESULT_ARRAY[3] = 0;
    }

    private void ClearInputMessageSkip()
    {
        if (!inputMessageSkipActive)
            return;

        inputMessageSkipActive = false;
        MesSkip = false;
    }

    /// <summary>
    /// Preserves the non-blocking state mutation performed by the desktop
    /// ReadAnyKey path when TWAIT replaces it with a timed request immediately.
    /// </summary>
    public void SuppressEventComEndWait()
    {
        if (GlobalStatic.Process is not null)
            GlobalStatic.Process.NeedWaitToEventComEnd = false;
    }

    public void Quit()
    {
        ProjectTooltipResources();
        isRunning = false;
    }
    public void ForceQuit() => Quit();
    public void ThrowError(bool playSound)
    {
        hasFatalError = true;
        FlushFatalDiagnosticLines();
        Quit();
    }
    public void ThrowTitleError(bool error)
    {
        hasFatalError = true;
        FlushFatalDiagnosticLines();
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
    public void SetFont(string fontName)
    {
        if (!string.IsNullOrWhiteSpace(fontName) && !string.Equals(fontName, Config.FontName, StringComparison.Ordinal) && ignoredGameFonts.Add(fontName))
            RecordWarning($"game_font_ignored:{fontName[..Math.Min(fontName.Length, 128)]}");
        stringStyle.Fontname = Config.FontName;
    }
    public void SetBgColor(Color color)
    {
        bgColor = color;
        if (outputEnabled && adapter is StructuredGameConsole)
            EmitStructured(ConsoleOperation.SetWindow(CurrentWindowMetadata()));
    }
    public void SetWindowTitle(string value)
    {
        windowTitle = value ?? string.Empty;
        if (outputEnabled)
            EmitStructured(ConsoleOperation.SetWindow(CurrentWindowMetadata()));
    }
    public string GetWindowTitle() => windowTitle;
    public void UpdateGeneration()
    {
        activeButtonGeneration = nextButtonGeneration;
        updatedGeneration = true;
    }

    internal void UpdateLegacyGeneration() => UpdateGeneration();

    public void forceUpdateGeneration()
    {
        nextButtonGeneration = checked(nextButtonGeneration + 1);
        activeButtonGeneration = nextButtonGeneration;
        updatedGeneration = true;
    }
    public bool ButtonIsSelected(ConsoleButtonString button) => false;
    public bool ButtonIsPointing(ConsoleButtonString button) => false;
    public Point GetMousePosition() => mousePosition;
    public bool MoveMouse(Point point)
    {
        // MoveMouse receives a top-left client coordinate from the pinned
        // desktop event path. There is no headless hover-state bitmap to
        // refresh, but retaining the coordinate is still required for the
        // next ERB MOUSEX()/MOUSEY() evaluation.
        mousePosition = new Point(point.X, checked(point.Y - viewportHeight));
        return false;
    }

    public ConsoleDisplayLine[] GetDisplayLines(long lineNo) => DisplayLineList.ToArray();

    /// <summary>
    /// Implements the upstream PRINT-buffer pop used by
    /// HTML_POPPRINTINGSTR(). The structured renderer keeps its pending
    /// nodes outside PrintStringBuffer, so returning DisplayLineList here
    /// would pop already committed browser output and would lose the rich
    /// text/image parts that the HTML helper needs.
    /// </summary>
    public ConsoleDisplayLine[] PopDisplayingLines()
    {
        if (!outputEnabled || (pendingLine.Count == 0 && pendingBufferedLines.Count == 0))
            return null;

        var result = new List<ConsoleDisplayLine>(pendingBufferedLines.Count + 1);
        foreach (PendingBufferedLine line in pendingBufferedLines)
            AppendPoppedPrintingLine(result, line);
        if (pendingLine.Count > 0)
        {
            AppendPoppedPrintingLine(result, new PendingBufferedLine(
                pendingLine.ToArray(),
                pendingLineAlignment,
                pendingLineNoWrap,
                Truncate: false,
                pendingLineEnd));
        }

        pendingBufferedLines.Clear();
        pendingLine.Clear();
        pendingLineAlignment = null;
        pendingLineNoWrap = false;
        pendingLineEnd = true;
        return result.Count == 0 ? null : result.ToArray();
    }

    private void AppendPoppedPrintingLine(
        List<ConsoleDisplayLine> destination,
        PendingBufferedLine line)
    {
        ConsoleButtonString[] buttons = CreateUpstreamPrintingButtons(line.Nodes);
        if (buttons.Length == 0)
            return;

        destination.Add(new ConsoleDisplayLine(
            buttons,
            isLogical: true,
            temporary: false,
            lineEnd: line.LineEnd));
    }

    private ConsoleButtonString[] CreateUpstreamPrintingButtons(IReadOnlyList<ConsoleNode> nodes)
    {
        var buttons = new List<ConsoleButtonString>();
        var plainParts = new List<AConsoleDisplayNode>();

        void FlushPlainParts()
        {
            if (plainParts.Count == 0)
                return;
            buttons.Add(new ConsoleButtonString(this, plainParts.ToArray()));
            plainParts.Clear();
        }

        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case ButtonNode button:
                {
                    AConsoleDisplayNode[] parts = ToUpstreamPrintingParts(button.Children);
                    if (parts.Length == 0)
                        break;

                    FlushPlainParts();
                    ConsoleButtonString legacy = CreateUpstreamPrintingButton(button, parts);
                    if (legacy is not null)
                        buttons.Add(legacy);
                    break;
                }
                case PositionedInlineSegmentNode segment when segment.Action is not null:
                {
                    AConsoleDisplayNode[] parts = ToUpstreamPrintingParts(segment.Children);
                    if (parts.Length == 0)
                        break;

                    FlushPlainParts();
                    buttons.Add(new ConsoleButtonString(this, parts, segment.Action.Value));
                    break;
                }
                case LineBreakNode:
                    FlushPlainParts();
                    break;
                default:
                {
                    AConsoleDisplayNode? part = ToUpstreamPrintingPart(node);
                    if (part is not null)
                        plainParts.Add(part);
                    break;
                }
            }
        }

        FlushPlainParts();
        return buttons.ToArray();
    }

    private ConsoleButtonString CreateUpstreamPrintingButton(
        ButtonNode button,
        AConsoleDisplayNode[] parts)
    {
        ConsoleButtonString legacy;
        if (!button.Enabled)
        {
            if (button.Tooltip is null && button.PositionX is null)
            {
                var plain = new ConsoleButtonString(this, parts);
                return plain;
            }
            legacy = new ConsoleButtonString(this, parts);
        }
        else if (integerButtonNodes.Contains(button) &&
            long.TryParse(button.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long input))
        {
            legacy = new ConsoleButtonString(this, parts, input);
        }
        else
        {
            legacy = new ConsoleButtonString(this, parts, button.Value);
        }

        if (button.Tooltip is not null)
            legacy.Title = button.Tooltip;
        if (button.PositionX is int positionX)
            legacy.LockPointX(positionX);
        return legacy;
    }

    private AConsoleDisplayNode[] ToUpstreamPrintingParts(IReadOnlyList<ConsoleNode> nodes)
    {
        var parts = new List<AConsoleDisplayNode>(nodes.Count);
        foreach (ConsoleNode node in nodes)
        {
            if (node is PositionedInlineSegmentNode segment)
            {
                parts.AddRange(ToUpstreamPrintingParts(segment.Children));
                continue;
            }

            AConsoleDisplayNode? part = ToUpstreamPrintingPart(node);
            if (part is not null)
                parts.Add(part);
        }
        return parts.ToArray();
    }

    private AConsoleDisplayNode? ToUpstreamPrintingPart(ConsoleNode node) => node switch
    {
        TextNode text => new ConsoleStyledString(text.Text, ToUpstreamStringStyle(text.Style)),
        SpriteNode sprite => ToUpstreamPrintingImage(sprite),
        ShapeNode shape => ToUpstreamPrintingShape(shape),
        ImageNode image => new ConsoleStyledString(image.AltText ?? string.Empty, stringStyle),
        _ => null,
    };

    private AConsoleDisplayNode ToUpstreamPrintingImage(SpriteNode sprite)
    {
        string source = string.IsNullOrEmpty(sprite.AltText) ? sprite.AssetId.Value : sprite.AltText;
        ConsoleRect destination = sprite.Destination;
        return new ConsoleImagePart(
            source,
            null,
            null,
            PixelLength(destination.Height),
            PixelLength(destination.Width),
            PixelLength(destination.Y));
    }

    private AConsoleDisplayNode? ToUpstreamPrintingShape(ShapeNode shape)
    {
        string shapeName;
        MixedNum[] parameters;
        switch (shape.Shape)
        {
            case ConsoleShapeKind.Space:
                shapeName = "space";
                parameters = [PixelLength(shape.Bounds.Width)];
                break;
            case ConsoleShapeKind.Rectangle:
                shapeName = "rect";
                parameters =
                [
                    PixelLength(shape.Bounds.X),
                    PixelLength(shape.Bounds.Y),
                    PixelLength(shape.Bounds.Width),
                    PixelLength(shape.Bounds.Height)
                ];
                break;
            default:
                return null;
        }

        return ConsoleShapePart.CreateShape(
            shapeName,
            parameters,
            ToDrawingColor(shape.Fill, Config.ForeColor),
            ToDrawingColor(shape.ButtonColor, Config.FocusColor),
            shape.Fill is not null);
    }

    private static MixedNum PixelLength(int value) => new() { num = value, isPx = true };

    private static StringStyle ToUpstreamStringStyle(ConsoleTextStyle style)
    {
        Color foreground = ToDrawingColor(style.Foreground, Config.ForeColor);
        Color buttonColor = ToDrawingColor(style.ButtonColor, Config.FocusColor);
        FontStyle fontStyle = FontStyle.Regular;
        if ((style.Decorations & ConsoleFontStyle.Bold) != 0)
            fontStyle |= FontStyle.Bold;
        if ((style.Decorations & ConsoleFontStyle.Italic) != 0)
            fontStyle |= FontStyle.Italic;
        if ((style.Decorations & ConsoleFontStyle.Underline) != 0)
            fontStyle |= FontStyle.Underline;
        if ((style.Decorations & ConsoleFontStyle.Strike) != 0)
            fontStyle |= FontStyle.Strikeout;

        return new StringStyle(
            foreground,
            style.Foreground is not null && foreground != Config.ForeColor,
            buttonColor,
            fontStyle,
            Config.FontName);
    }

    private static Color ToDrawingColor(
        CloudEmuera.RuntimeAdapter.ConsoleColor? color,
        Color fallback) => color is { } value
            ? Color.FromArgb(value.Alpha, value.Red, value.Green, value.Blue)
            : fallback;
    public int GetLinePointY(int lineNo) => checked(lineNo * 16);
    public string getDefStBar() => barString;
    public string getStBar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        int target = Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.DrawableWidth > 0
            ? MinorShift.Emuera.Runtime.Config.Config.DrawableWidth
            : viewportWidth);
        var builder = new System.Text.StringBuilder();
        int width = 0;
        while (width < target)
        {
            builder.Append(value);
            width = Measure(builder.ToString());
        }
        while (width > target && builder.Length > 0)
        {
            builder.Length--;
            width = Measure(builder.ToString());
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
        if (ContainsSelectableAction(translated.Nodes))
            UpdateGeneration();
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
        RuntimeSpriteDefinition resolved = ResolveSpriteDefinition(name);
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
        RuntimeSpriteDefinition resolved = ResolveSpriteDefinition(image.Name);
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
            tooltip: tooltip is null ? null : DisplayText(tooltip))));
        return true;
    }
    public void SetRedraw(params object[] args) => redrawIntervalMilliseconds = args.Length == 0 ? 0 : Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
    public void setRedrawTimer(params object[] args) => redrawIntervalMilliseconds = args.Length == 0 ? 0 : Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
    public void ReloadErbFinished() { }
    // CloudEmuera ADR-0036: TOOLTIP_* is browser presentation state, not a
    // desktop-host shim. Keep this vendored seam thin and strongly typed.
    public void CustomToolTip(bool enabled) => TooltipSink.SetTooltipCustom(enabled);
    public void SetToolTipColor(Color foreground, Color background) => TooltipSink.SetTooltipColor(
        new CloudEmuera.RuntimeAdapter.ConsoleColor(foreground.R, foreground.G, foreground.B),
        new CloudEmuera.RuntimeAdapter.ConsoleColor(background.R, background.G, background.B));
    public void SetToolTipDelay(int delay) => TooltipSink.SetTooltipDelay(delay);
    public void SetToolTipDuration(int duration) => TooltipSink.SetTooltipDuration(duration);
    public void SetToolTipFontName(string name)
    {
        if (!string.Equals(name, Config.FontName, StringComparison.Ordinal) && ignoredGameFonts.Add(name ?? string.Empty))
            RecordWarning($"Tooltip font '{name}' is mapped to the Session font.");
        TooltipSink.SetTooltipFont(name ?? string.Empty);
    }
    public void SetToolTipFontSize(long size) => TooltipSink.SetTooltipFontSize(size);
    public void SetToolTipFormat(long flags) => TooltipSink.SetTooltipFormat(flags);
    public void SetToolTipImg(bool enabled)
    {
        TooltipSink.SetTooltipImageMode(enabled);
        ProjectTooltipResources();
    }

    private ITooltipStateSink TooltipSink => adapter as ITooltipStateSink
        ?? throw new InvalidOperationException("The structured runtime adapter does not provide tooltip state.");

    private void AppendText(string value)
    {
        if (outputEnabled && !string.IsNullOrEmpty(value))
            pendingLine.Add(new TextNode(DisplayText(value), ToConsoleTextStyle()));
    }

    private void EmitText(string value) => AppendText(value);

    private void EmitButton(string label, string input, bool isInteger)
    {
        if (outputEnabled && !string.IsNullOrEmpty(label))
        {
            ButtonNode button = new(
                [new TextNode(DisplayText(label), ToConsoleTextStyle())],
                input,
                generation: nextButtonGeneration);
            if (isInteger)
                integerButtonNodes.Add(button);
            AppendNode(button);
            UpdateGeneration();
        }
    }

    private void EmitPrintCButton(string value, string input, bool alignmentRight, bool isInteger)
    {
        if (!outputEnabled || string.IsNullOrEmpty(value))
            return;

        string formatted = FormatPrintCValue(value, alignmentRight);
        // The real layout path keeps PRINTBUTTONC/PRINTBUTTONLC padding out of
        // the action box. The compatibility path without a bound runtime font
        // retains the old logical ButtonNode projection used by legacy host
        // fixtures; Workers always bind a catalogued face before reaching this
        // branch.
        if (stringMeasure is null || Config.DefaultFont is null)
        {
            EmitButton(formatted, input, isInteger);
            return;
        }

        int labelStart = alignmentRight ? formatted.Length - value.Length : 0;
        string leading = formatted[..labelStart];
        string trailing = formatted[(labelStart + value.Length)..];
        AppendText(leading);
        EmitButton(value, input, isInteger);
        AppendText(trailing);
        pendingLineEnd = true;
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
            FlushPendingLine(force: true, temporary: temporary, noWrap: true, truncate: true);
        else
        {
            AppendText(value);
            pendingLineEnd = true;
            FlushPendingLine(force: true, temporary: temporary, noWrap: true, truncate: true);
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

    private int Measure(string value)
    {
        if (stringMeasure is not null && Config.DefaultFont is not null)
            return stringMeasure.GetDisplayLength(value, Config.DefaultFont);
        return checked(value.Length * Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize / 2));
    }

    private int MeasureWithStyle(string value, ConsoleTextStyle style)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        if (stringMeasure is null)
            return Measure(value);

        FontStyle fontStyle = FontStyle.Regular;
        if ((style.Decorations & ConsoleFontStyle.Bold) != 0) fontStyle |= FontStyle.Bold;
        if ((style.Decorations & ConsoleFontStyle.Italic) != 0) fontStyle |= FontStyle.Italic;
        Font? font = FontFactory.GetFont(Config.FontName, fontStyle);
        if (font is null)
            throw new InvalidOperationException("The selected runtime font could not create a measurement font.");
        return stringMeasure.GetDisplayLength(value, font);
    }

    private IReadOnlyList<ConsoleLine> LayoutPhysicalLines(
        string logicalLineId,
        IReadOnlyList<ConsoleNode> nodes,
        ConsoleLineAlignment alignment,
        bool temporary,
        bool noWrap,
        bool truncate,
        bool lineEnd,
        ISet<ButtonNode>? physicalPositionButtons = null)
    {
        int layoutWidth = Config.DrawableWidth > 0 ? Config.DrawableWidth : viewportWidth;
        int lineHeight = Math.Max(Config.FontSize, Config.LineHeight);
        var atoms = CreateLayoutAtoms(nodes, physicalPositionButtons);
        var drafts = new List<PhysicalLineDraft>();
        var current = new List<PositionedInlineSegmentNode>();
        // `cursor` is the origin for the next unpositioned atom. Explicit
        // positions may move it backwards, while `contentWidth` remains the
        // maximum painted extent used for line alignment.
        int cursor = 0;
        int contentWidth = 0;

        void FlushDraft()
        {
            drafts.Add(new PhysicalLineDraft(current.ToArray(), contentWidth));
            current.Clear();
            cursor = 0;
            contentWidth = 0;
        }

        // PRINTSINGLE* uses the upstream single-row buffer, but a browser
        // physical line must not be allowed to overflow the drawable viewport.
        // Keep the prefix that fits and discard the rest instead of entering
        // the normal wrapping loop.
        if (truncate)
        {
            noWrap = true;
            foreach (LayoutAtom atom in atoms)
            {
                int position = ResolveAtomPosition(cursor, atom);
                bool fits = layoutWidth <= 0 || position + atom.Width <= layoutWidth;
                if (fits)
                {
                    current.Add(new PositionedInlineSegmentNode(position, atom.Width, atom.Children, atom.Action));
                    cursor = checked(position + atom.Width);
                    contentWidth = Math.Max(contentWidth, cursor);
                    continue;
                }

                if (atom.CanDivide)
                {
                    int available = Math.Max(0, layoutWidth - position);
                    int fittingCharacters = FindFittingCharacters(atom, available);
                    if (fittingCharacters > 0 && TrySplitAtom(atom, fittingCharacters, out LayoutAtom? prefix, out _))
                    {
                        current.Add(new PositionedInlineSegmentNode(position, prefix.Width, prefix.Children, prefix.Action));
                        cursor = checked(position + prefix.Width);
                        contentWidth = Math.Max(contentWidth, cursor);
                    }
                }
                break;
            }
        }
        else
        {
            foreach (LayoutAtom original in atoms)
            {
                LayoutAtom remaining = original;
                while (true)
                {
                    int position = ResolveAtomPosition(cursor, remaining);
                    bool fits = noWrap || layoutWidth <= 0 || position + remaining.Width <= layoutWidth;
                    if (fits)
                    {
                        current.Add(new PositionedInlineSegmentNode(position, remaining.Width, remaining.Children, remaining.Action));
                        cursor = checked(position + remaining.Width);
                        contentWidth = Math.Max(contentWidth, cursor);
                        break;
                    }

                    if (current.Count > 0 && remaining.Action is not null && Config.ButtonWrap)
                    {
                        FlushDraft();
                        continue;
                    }

                    if (remaining.CanDivide)
                    {
                        int available = Math.Max(0, layoutWidth - position);
                        int fittingCharacters = FindFittingCharacters(remaining, available);
                        if (fittingCharacters > 0 && TrySplitAtom(remaining, fittingCharacters, out LayoutAtom? prefix, out LayoutAtom? suffix))
                        {
                            current.Add(new PositionedInlineSegmentNode(position, prefix.Width, prefix.Children, prefix.Action));
                            cursor = checked(position + prefix.Width);
                            contentWidth = Math.Max(contentWidth, cursor);
                            FlushDraft();
                            remaining = suffix;
                            continue;
                        }
                    }

                    if (current.Count > 0)
                    {
                        FlushDraft();
                        continue;
                    }

                    // A non-dividable image/shape or a single glyph wider than the
                    // drawable area remains on its own physical line, matching the
                    // upstream overflow rule instead of looping forever.
                    current.Add(new PositionedInlineSegmentNode(0, remaining.Width, remaining.Children, remaining.Action));
                    cursor = remaining.Width;
                    contentWidth = Math.Max(contentWidth, cursor);
                    break;
                }
            }
        }

        if (current.Count > 0 || drafts.Count == 0 || lineEnd)
            FlushDraft();

        var result = new List<ConsoleLine>(drafts.Count);
        for (int index = 0; index < drafts.Count; index++)
        {
            PhysicalLineDraft draft = drafts[index];
            int shift = alignment switch
            {
                ConsoleLineAlignment.Center => Math.Max(0, (layoutWidth - draft.ContentWidth) / 2),
                ConsoleLineAlignment.Right => Math.Max(0, layoutWidth - draft.ContentWidth),
                _ => 0
            };
            IReadOnlyList<ConsoleNode> positioned = draft.Segments
                .Select(segment => (ConsoleNode)new PositionedInlineSegmentNode(
                    checked(segment.PositionX + shift),
                    segment.MeasuredWidth,
                    segment.Children,
                    segment.Action))
                .ToArray();
            string physicalId = index == 0 ? logicalLineId : $"{logicalLineId}-p{index}";
            result.Add(new ConsoleLine(
                physicalId,
                positioned,
                alignment,
                temporary,
                noWrap,
                layoutWidth,
                lineHeight,
                logicalLineId,
                index,
                index == 0));
        }
        return result;
    }

    private int ResolveAtomPosition(int cursor, LayoutAtom atom)
    {
        // HtmlManager keeps the exact source `button pos` value in
        // RelativePointX. It is expressed in hundredths of the configured
        // font size, while PositionedInlineSegmentNode is a physical-pixel
        // contract. Convert only semantic ButtonNodes here; an already
        // positioned segment is already measured and must not be scaled a
        // second time. Explicit positions may move backwards to compose
        // several portrait layers at one origin.
        int? lockedX = atom.LockedX;
        if (atom.LockedXIsRelative && lockedX is { } relativeX)
            lockedX = checked((int)((long)relativeX * Config.FontSize / 100));
        return lockedX ?? cursor;
    }

    private IReadOnlyList<LayoutAtom> CreateLayoutAtoms(
        IReadOnlyList<ConsoleNode> nodes,
        ISet<ButtonNode>? physicalPositionButtons = null)
    {
        var atoms = new List<LayoutAtom>(nodes.Count);
        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    atoms.Add(new LayoutAtom([text], MeasureWithStyle(text.Text, text.Style), true, text.Text, text.Style, null, null, false));
                    break;
                case ButtonNode button:
                    atoms.Add(new LayoutAtom(
                        button.Children,
                        MeasureInlineNodes(button.Children),
                        button.Children.All(child => child is TextNode),
                        button.Children.OfType<TextNode>().Any() ? string.Concat(button.Children.OfType<TextNode>().Select(child => child.Text)) : null,
                        button.Children.OfType<TextNode>().Select(child => child.Style).Distinct().Count() == 1 ? button.Children.OfType<TextNode>().First().Style : null,
                        new ConsoleInlineAction(button.Value, button.Tooltip is null ? null : DisplayText(button.Tooltip), button.Enabled, button.Generation),
                        button.PositionX,
                        physicalPositionButtons is null || !physicalPositionButtons.Contains(button)));
                    break;
                case PositionedInlineSegmentNode segment:
                    atoms.Add(new LayoutAtom(segment.Children, segment.MeasuredWidth, false, null, null, segment.Action, segment.PositionX, false));
                    break;
                default:
                    atoms.Add(new LayoutAtom([node], MeasureInlineNode(node), false, null, null, null, null, false));
                    break;
            }
        }
        return atoms;
    }

    private int MeasureInlineNodes(IEnumerable<ConsoleNode> nodes) => checked(nodes.Sum(MeasureInlineNode));

    private int MeasureInlineNode(ConsoleNode node) => node switch
    {
        TextNode text => MeasureWithStyle(text.Text, text.Style),
        ButtonNode button => MeasureInlineNodes(button.Children),
        PositionedInlineSegmentNode segment => segment.MeasuredWidth,
        ImageNode image => image.Destination?.Width ?? image.Width ?? (image.AltText is null ? 0 : Measure(image.AltText)),
        SpriteNode sprite => sprite.Destination.Width,
        ShapeNode shape => shape.Bounds.Width,
        // ConsoleDivPart keeps its rectangle width for painting but does not
        // contribute that width to ConsoleButtonString's inline cursor. HTML
        // divs therefore form positioned overlay layers; counting Bounds.Width
        // here shifts every following sibling and breaks multi-panel layouts.
        DivNode => 0,
        HtmlIslandNode island when island.StructuredNodes is { } structured => MeasureInlineNodes(structured),
        HtmlIslandNode island when island.Layout is { } layout => layout.Width,
        HtmlIslandNode => 0,
        LineBreakNode => 0,
        _ => 0
    };

    private int FindFittingCharacters(LayoutAtom atom, int available)
    {
        if (!atom.CanDivide || atom.Text is null || atom.Text.Length == 0 || available <= 0)
            return 0;
        int low = 0;
        int high = atom.Text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (MeasureTextPrefix(atom, middle) <= available)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }

    private bool TrySplitAtom(LayoutAtom atom, int characterCount, out LayoutAtom prefix, out LayoutAtom suffix)
    {
        prefix = null!;
        suffix = null!;
        if (!atom.CanDivide || atom.Text is null || characterCount <= 0 || characterCount >= atom.Text.Length)
            return false;

        IReadOnlyList<ConsoleNode> prefixChildren;
        IReadOnlyList<ConsoleNode> suffixChildren;
        if (atom.Children.All(child => child is TextNode))
        {
            TextNode[] textChildren = atom.Children.Cast<TextNode>().ToArray();
            prefixChildren = SliceStyledText(textChildren, 0, characterCount);
            suffixChildren = SliceStyledText(textChildren, characterCount, atom.Text.Length - characterCount);
        }
        else
        {
            prefixChildren = [new TextNode(atom.Text[..characterCount], atom.TextStyle)];
            suffixChildren = [new TextNode(atom.Text[characterCount..], atom.TextStyle)];
        }

        int prefixWidth = MeasureInlineNodes(prefixChildren);
        int suffixWidth = MeasureInlineNodes(suffixChildren);
        prefix = atom with { Children = prefixChildren, Text = atom.Text[..characterCount], Width = prefixWidth, LockedX = atom.LockedX };
        suffix = atom with { Children = suffixChildren, Text = atom.Text[characterCount..], Width = suffixWidth, LockedX = null };
        return true;
    }

    private int MeasureTextPrefix(LayoutAtom atom, int characterCount)
    {
        if (atom.Children.All(child => child is TextNode))
            return MeasureInlineNodes(SliceStyledText(atom.Children.Cast<TextNode>().ToArray(), 0, characterCount));
        return atom.TextStyle is null ? 0 : MeasureWithStyle(atom.Text![..characterCount], atom.TextStyle);
    }

    /// <summary>
    /// Reflows the complete last logical line when a non-terminated PRINT
    /// buffer receives more text. Appending the newly measured segments at the
    /// old x coordinate would preserve a stale wrap decision and could leave
    /// the browser with two incompatible physical-line layouts. Replacements,
    /// additions and removals are deliberately committed as one transaction.
    /// </summary>
    private void RelayoutLastLogicalLine(StructuredGameConsole console, IReadOnlyList<ConsoleNode> appendedNodes, PendingBufferedLine pending)
    {
        ConsoleLine last = console.Snapshot.Scrollback.LastOrDefault(line => string.Equals(line.LineId, lastPhysicalLineId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The last physical line disappeared before relayout.");
        string logicalId = last.LogicalLineId ?? last.LineId;
        List<ConsoleLine> existingGroup = console.Snapshot.Scrollback
            .Where(line => string.Equals(line.LogicalLineId ?? line.LineId, logicalId, StringComparison.Ordinal))
            .OrderBy(line => line.PhysicalIndex)
            .ToList();
        if (existingGroup.Count == 0)
            throw new InvalidOperationException("The last logical line has no physical lines.");

        var originalNodes = new List<ConsoleNode>();
        var physicalPositionButtons = new HashSet<ButtonNode>();
        foreach (ConsoleLine line in existingGroup)
        {
            foreach (PositionedInlineSegmentNode segment in line.Nodes.OfType<PositionedInlineSegmentNode>())
            {
                if (segment.Action is { } action)
                {
                    ButtonNode button = new(
                        segment.Children,
                        action.Value,
                        action.Tooltip,
                        action.Enabled,
                        action.Generation,
                        segment.PositionX);
                    originalNodes.Add(button);
                    physicalPositionButtons.Add(button);
                }
                else
                    originalNodes.AddRange(segment.Children);
            }
        }
        originalNodes.AddRange(appendedNodes);
        // `appendedNodes` already passed through AutoButtonize in
        // FlushPendingLine. Re-parsing the reconstructed ButtonNode label
        // would allow button text that happens to contain a bracketed token
        // to become a second action, changing the upstream click contract.
        IReadOnlyList<ConsoleNode> relayoutInput = originalNodes;
        IReadOnlyList<ConsoleLine> relaidOut = LayoutPhysicalLines(
            logicalId,
            relayoutInput,
            existingGroup[0].Alignment,
            existingGroup[0].Temporary,
            existingGroup[0].NoWrap,
            pending.Truncate,
            pending.LineEnd,
            physicalPositionButtons);

        var oldIds = existingGroup.Select(line => line.LineId).ToHashSet(StringComparer.Ordinal);
        var newIds = relaidOut.Select(line => line.LineId).ToHashSet(StringComparer.Ordinal);
        var operations = new List<ConsoleOperation>(relaidOut.Count + oldIds.Count);
        foreach (ConsoleLine line in relaidOut)
        {
            operations.Add(oldIds.Contains(line.LineId)
                ? ConsoleOperation.ReplaceLine(line)
                : ConsoleOperation.AppendLine(line));
        }
        string[] removed = existingGroup
            .Where(line => !newIds.Contains(line.LineId))
            .Select(line => line.LineId)
            .ToArray();
        if (removed.Length > 0)
            operations.Add(ConsoleOperation.DeleteLines(removed));
        EmitStructuredTransaction(operations);
        lastLineId = relaidOut[^1].LineId;
        lastPhysicalLineId = relaidOut[^1].LineId;
        lastLineTemporary = relaidOut[^1].Temporary;
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
                    Truncate: false,
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
        bool? noWrap = null,
        bool truncate = false)
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
                truncate,
                pendingLineEnd));
        }

        foreach (PendingBufferedLine line in lines)
        {
            IReadOnlyList<ConsoleNode> projectedNodes = AutoButtonize(line.Nodes);
            if (line.Truncate && (stringMeasure is null || Config.DefaultFont is null))
                projectedNodes = TruncateNodes(projectedNodes);
            if (stringMeasure is not null && Config.DefaultFont is not null)
            {
                string? deferredLogicalLineId = deferredReplacementLogicalLineId;
                string id = deferredLogicalLineId ?? $"emuera-line-{checked(++lineId):x}";
                IReadOnlyList<ConsoleLine> physicalLines = LayoutPhysicalLines(
                    id,
                    projectedNodes,
                    line.Alignment ?? ToAlignment(),
                    temporary,
                    line.NoWrap,
                    line.Truncate,
                    line.LineEnd);
                if (lastLineCanAppend && lastPhysicalLineId is not null && projectedNodes.Count > 0 &&
                    adapter is StructuredGameConsole appendConsole &&
                    appendConsole.Snapshot.Scrollback.Any(existing => string.Equals(existing.LineId, lastPhysicalLineId, StringComparison.Ordinal)))
                {
                    RelayoutLastLogicalLine(appendConsole, projectedNodes, line);
                    CommitLegacyDisplayLine(projectedNodes, temporary, line.LineEnd, appendToPrevious: true);
                    lastLineCanAppend = !line.LineEnd;
                    continue;
                }

                IReadOnlyList<ConsoleLine> existingReplacementGroup = deferredLogicalLineId is not null &&
                    adapter is StructuredGameConsole replacementConsole
                    ? GetLogicalLineGroup(replacementConsole, deferredLogicalLineId)
                    : [];
                var oldIds = existingReplacementGroup
                    .Select(line => line.LineId)
                    .ToHashSet(StringComparer.Ordinal);
                var newIds = physicalLines
                    .Select(line => line.LineId)
                    .ToHashSet(StringComparer.Ordinal);
                var operations = new List<ConsoleOperation>(physicalLines.Count + existingReplacementGroup.Count);
                for (int index = 0; index < physicalLines.Count; index++)
                {
                    ConsoleLine physicalLine = physicalLines[index];
                    if (oldIds.Contains(physicalLine.LineId))
                        operations.Add(ConsoleOperation.ReplaceLine(physicalLine));
                    else
                        operations.Add(ConsoleOperation.AppendLine(physicalLine));
                }
                string[] removedReplacementLines = existingReplacementGroup
                    .Where(line => !newIds.Contains(line.LineId))
                    .Select(line => line.LineId)
                    .ToArray();
                if (removedReplacementLines.Length > 0)
                    operations.Add(ConsoleOperation.DeleteLines(removedReplacementLines));
                EmitStructuredTransaction(operations);
                deferredReplacementLogicalLineId = null;
                lastLineId = physicalLines[^1].LineId;
                lastPhysicalLineId = physicalLines[^1].LineId;
                lastLineTemporary = temporary;
                if (existingReplacementGroup.Count == 0)
                    logicalLineCount = checked(logicalLineCount + 1);
                CommitLegacyDisplayLine(projectedNodes, temporary, line.LineEnd, appendToPrevious: false);
                lastLineCanAppend = !line.LineEnd;
                continue;
            }
            if (lastLineCanAppend && lastLineId is not null && projectedNodes.Count > 0)
            {
                EmitStructured(ConsoleOperation.AppendInline(lastLineId, projectedNodes));
                CommitLegacyDisplayLine(projectedNodes, temporary, line.LineEnd, appendToPrevious: true);
            }
            else
            {
                string id = deferredReplacementLogicalLineId ?? $"emuera-line-{checked(++lineId):x}";
                ConsoleLine replacement = new(
                    id,
                    projectedNodes,
                    line.Alignment ?? ToAlignment(),
                    temporary,
                    line.NoWrap);
                bool replaced = false;
                if (deferredReplacementLogicalLineId is not null)
                {
                    bool canReplace = adapter is StructuredGameConsole replacementConsole &&
                        replacementConsole.Snapshot.Scrollback.Any(existing => string.Equals(existing.LineId, id, StringComparison.Ordinal));
                    if (canReplace)
                    {
                        EmitStructured(ConsoleOperation.ReplaceLine(replacement));
                        replaced = true;
                    }
                    else
                        EmitStructured(ConsoleOperation.AppendLine(replacement));
                    deferredReplacementLogicalLineId = null;
                }
                else
                    EmitStructured(ConsoleOperation.AppendLine(replacement));
                lastLineId = id;
                lastPhysicalLineId = id;
                lastLineTemporary = temporary;
                if (!replaced)
                    logicalLineCount = checked(logicalLineCount + 1);
                CommitLegacyDisplayLine(projectedNodes, temporary, line.LineEnd, appendToPrevious: false);
            }

            lastLineCanAppend = !line.LineEnd;
        }

        pendingBufferedLines.Clear();
        pendingLine.Clear();
        pendingLineAlignment = null;
        pendingLineNoWrap = false;
        pendingLineEnd = true;
    }

    private IReadOnlyList<ConsoleNode> TruncateNodes(IReadOnlyList<ConsoleNode> nodes)
    {
        int layoutWidth = Config.DrawableWidth > 0 ? Config.DrawableWidth : viewportWidth;
        var result = new List<ConsoleNode>(nodes.Count);
        int cursor = 0;
        foreach (ConsoleNode node in nodes)
        {
            int width = MeasureInlineNode(node);
            if (cursor + width <= layoutWidth)
            {
                result.Add(node);
                cursor = checked(cursor + width);
                continue;
            }

            // Preserve a text prefix when the fallback path has no bound font.
            // Buttons and drawable nodes remain atomic, as they are in the
            // authoritative layout path.
            if (node is TextNode)
            {
                LayoutAtom atom = CreateLayoutAtoms([node])[0];
                int fittingCharacters = FindFittingCharacters(atom, Math.Max(0, layoutWidth - cursor));
                if (fittingCharacters > 0 && TrySplitAtom(atom, fittingCharacters, out LayoutAtom? prefix, out _))
                    result.AddRange(prefix.Children);
            }
            break;
        }
        return result;
    }

    private void FlushDeferredReplacementDelete()
    {
        if (deferredReplacementLogicalLineId is null || adapter is not StructuredGameConsole structured)
            return;

        string logicalLineId = deferredReplacementLogicalLineId;
        deferredReplacementLogicalLineId = null;
        IReadOnlyList<ConsoleLine> group = GetLogicalLineGroup(structured, logicalLineId);
        if (group.Count == 0)
            return;

        EmitStructured(ConsoleOperation.DeleteLines(group.Select(line => line.LineId).ToArray()));
        deletedLines = checked(deletedLines + 1);
        lastLineId = structured.Snapshot.Scrollback.LastOrDefault()?.LineId;
        lastPhysicalLineId = lastLineId;
        lastLineCanAppend = false;
        lastLineTemporary = structured.Snapshot.Scrollback.LastOrDefault()?.Temporary ?? false;
        logicalLineCount = Math.Max(0, logicalLineCount - 1);
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
                ButtonNode button = new(
                    children,
                    primitive.Input.ToString(CultureInfo.InvariantCulture),
                    generation: nextButtonGeneration);
                integerButtonNodes.Add(button);
                destination.Add(button);
                UpdateGeneration();
            }
            else
            {
                destination.AddRange(children);
            }
            offset = checked(offset + primitive.Str.Length);
        }
    }

    /// <summary>
    /// Keeps the pinned interpreter's legacy button inventory in step with
    /// the structured output inventory. BINPUT/BINPUTS do not inspect the
    /// browser-facing nodes; they inspect ConsoleDisplayLine.Buttons.
    /// </summary>
    private ConsoleDisplayLine CreateUpstreamDisplayLine(
        IReadOnlyList<ConsoleNode> nodes,
        bool temporary,
        bool lineEnd)
    {
        return new ConsoleDisplayLine(CreateUpstreamButtons(nodes), isLogical: true, temporary: temporary, lineEnd: lineEnd);
    }

    private ConsoleButtonString[] CreateUpstreamButtons(IReadOnlyList<ConsoleNode> nodes)
    {
        var buttons = new List<ConsoleButtonString>();
        foreach (ButtonNode button in EnumerateLegacyButtons(nodes))
        {
            if (!button.Enabled)
                continue;

            if (integerButtonNodes.Contains(button))
            {
                if (long.TryParse(button.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long input))
                    buttons.Add(new ConsoleButtonString(this, [], input));
                continue;
            }

            buttons.Add(new ConsoleButtonString(this, [], button.Value));
        }

        return [.. buttons];
    }

    private void CommitLegacyDisplayLine(
        IReadOnlyList<ConsoleNode> nodes,
        bool temporary,
        bool lineEnd,
        bool appendToPrevious)
    {
        IReadOnlyList<ConsoleNode> snapshot = nodes.ToArray();
        if (appendToPrevious && legacyDisplayLineNodes.Count > 0 && DisplayLineList.Count > 0)
        {
            IReadOnlyList<ConsoleNode> combined = legacyDisplayLineNodes[^1]
                .Concat(snapshot)
                .ToArray();
            legacyDisplayLineNodes[^1] = combined;
            ConsoleDisplayLine existing = DisplayLineList[^1];
            ConsoleButtonString[] appendedButtons = CreateUpstreamButtons(snapshot);
            existing.ChangeStr(existing.Buttons.Concat(appendedButtons).ToArray());
            existing.IsLineEnd = lineEnd;
            return;
        }

        legacyDisplayLineNodes.Add(snapshot);
        DisplayLineList.Add(CreateUpstreamDisplayLine(snapshot, temporary, lineEnd));
    }

    private void RemoveLegacyDisplayLines(int count)
    {
        int removeCount = Math.Min(count, Math.Min(legacyDisplayLineNodes.Count, DisplayLineList.Count));
        for (int index = 0; index < removeCount; index++)
        {
            foreach (ButtonNode button in EnumerateLegacyButtons(legacyDisplayLineNodes[^1]))
                integerButtonNodes.Remove(button);
            legacyDisplayLineNodes.RemoveAt(legacyDisplayLineNodes.Count - 1);
            DisplayLineList.RemoveAt(DisplayLineList.Count - 1);
        }
    }

    private static IEnumerable<ButtonNode> EnumerateLegacyButtons(IReadOnlyList<ConsoleNode> nodes)
    {
        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case ButtonNode button:
                    yield return button;
                    break;
                case DivNode div:
                    foreach (ButtonNode nested in EnumerateLegacyButtons(div.Children))
                        yield return nested;
                    break;
                case PositionedInlineSegmentNode segment:
                    foreach (ButtonNode nested in EnumerateLegacyButtons(segment.Children))
                        yield return nested;
                    break;
            }
        }
    }

    private static bool ContainsSelectableAction(IReadOnlyList<ConsoleNode> nodes)
    {
        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case ButtonNode { Enabled: true }:
                    return true;
                case PositionedInlineSegmentNode { Action.Enabled: true }:
                    return true;
                case DivNode div when ContainsSelectableAction(div.Children):
                    return true;
            }
        }

        return false;
    }

    private void PrepareInputButtonGeneration(InputType inputType)
    {
        bool integerInput = inputType is InputType.IntValue or InputType.IntButton;
        bool stringInput = inputType is InputType.StrValue or InputType.StrButton or InputType.AnyValue;
        if (!integerInput && !stringInput)
            return;

        // This is the pinned desktop newGeneration() boundary. A RESTART loop
        // that returns to the same TINPUT source line without printing a new
        // button deliberately keeps the existing active generation. eraAM's
        // 30 ms shop animation relies on that rule while it redraws only the
        // animation line and retains the command menu.
        LogicalLine currentInputLine = GlobalStatic.Process?.getCurrentLine;
        if (!updatedGeneration && currentInputLine != lastInputLine)
            activeButtonGeneration = nextButtonGeneration;
        else
            updatedGeneration = false;
        lastInputLine = currentInputLine;

        if (integerInput)
        {
            if (activeButtonGeneration == nextButtonGeneration)
                nextButtonGeneration = checked(nextButtonGeneration + 1);
            else if (!lastButtonInputWasInteger)
                activeButtonGeneration = nextButtonGeneration;
            lastButtonInputWasInteger = true;
        }
        else
        {
            if (activeButtonGeneration == nextButtonGeneration)
                nextButtonGeneration = checked(nextButtonGeneration + 1);
            else if (lastButtonInputWasInteger)
                activeButtonGeneration = nextButtonGeneration;
            lastButtonInputWasInteger = false;
        }

    }

    private string FormatPrintCValue(string value, bool alignmentRight)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        int printCWidth = MinorShift.Emuera.Runtime.Config.Config.PrintCLength;
        if (printCWidth <= 0)
            return value;

        // PRINTC counts Shift-JIS bytes, then trims only padding spaces that
        // exceed the measured width of exactly N default-font spaces. The
        // measure is session-owned; browser metrics never enter this path.
        int byteLength = printCByteCountEncoding.GetByteCount(value);
        int targetWidth = Measure(new string(' ', printCWidth));
        int styleWidth(string text) => MeasureWithStyle(text, ToConsoleTextStyle());
        if (alignmentRight && byteLength < printCWidth)
        {
            string padded = new string(' ', printCWidth - byteLength) + value;
            while (padded.Length > value.Length && styleWidth(padded) > targetWidth && padded[0] == ' ')
                padded = padded[1..];
            return padded;
        }

        if (!alignmentRight && byteLength < printCWidth + 1)
        {
            string padded = value + new string(' ', printCWidth + 1 - byteLength);
            while (padded.Length > value.Length && styleWidth(padded) > targetWidth && padded[^1] == ' ')
                padded = padded[..^1];
            return padded;
        }

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
        {
            SequencedConsoleTransaction transaction = structured.EmitTransaction(new ConsoleTransaction([operation]));
            RuntimeDebugTrace.Current?.RecordTransaction(transaction);
            ProjectTooltipResources();
        }
        else
            adapter.Emit(operation);
    }

    private void EmitStructuredTransaction(IEnumerable<ConsoleOperation> operations)
    {
        ConsoleOperation[] copy = operations.ToArray();
        if (copy.Length == 0)
            return;
        if (adapter is StructuredGameConsole structured)
        {
            SequencedConsoleTransaction transaction = structured.EmitTransaction(new ConsoleTransaction(copy));
            RuntimeDebugTrace.Current?.RecordTransaction(transaction);
            ProjectTooltipResources();
        }
        else
        {
            foreach (ConsoleOperation operation in copy)
                adapter.Emit(operation);
        }
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

    // GraphicsImage calls this for pixel mutations. Projection remains lazy:
    // only an id referenced by a visible tooltip is encoded at a display seam.
    internal void NotifyGraphicsMutation(int graphicsId, long revision)
    {
        if (adapter is StructuredGameConsole structured &&
            structured.StateStore.ContainsTooltipGraphicsReference(graphicsId))
            dirtyTooltipGraphics[graphicsId] = revision;
    }

    private void ProjectTooltipResources()
    {
        if (tooltipProjectionActive || adapter is not StructuredGameConsole structured)
            return;

        tooltipProjectionActive = true;
        try
        {
            ConsoleSnapshot snapshot = structured.Snapshot;
            IReadOnlyDictionary<int, int> references = structured.StateStore.TooltipGraphicsReferences;
            var projected = snapshot.TooltipResources.ToDictionary(resource => resource.GraphicsId);
            var operations = new List<ConsoleOperation>();
            IReadOnlyList<int> changedReferences = structured.StateStore.TakeTooltipProjectionCandidates();

            if (!snapshot.TooltipPresentation.ImageMode)
            {
                if (projected.Count > 0)
                    operations.Add(ConsoleOperation.ClearTooltipResources());
                dirtyTooltipGraphics.Clear();
                ApplyTooltipProjectionOperations(structured, operations);
                tooltipProjectionImageMode = false;
                return;
            }

            IEnumerable<int> candidates = tooltipProjectionImageMode
                ? changedReferences.Concat(dirtyTooltipGraphics.Keys)
                : references.Keys.Concat(projected.Keys);

            long projectedBytes = projected.Values.Sum(resource => (long)resource.PngData.Count);
            foreach (int graphicsId in candidates.Distinct().OrderBy(id => id))
            {
                projected.TryGetValue(graphicsId, out ConsoleTooltipResource? previous);
                if (!references.ContainsKey(graphicsId))
                {
                    if (previous is not null)
                    {
                        operations.Add(ConsoleOperation.RemoveTooltipResource(graphicsId));
                        projected.Remove(graphicsId);
                        projectedBytes -= previous.PngData.Count;
                    }
                    dirtyTooltipGraphics.Remove(graphicsId);
                    continue;
                }
                if (!AppContents.TryGetGraphics(graphicsId, out GraphicsImage? graphics) || !graphics.IsCreated)
                {
                    RemoveUnavailableTooltipResource(graphicsId, previous, projected, operations, ref projectedBytes, 0, "missing");
                    continue;
                }

                long revision = graphics.HeadlessRevision;
                if (previous?.Revision == revision && !dirtyTooltipGraphics.ContainsKey(graphicsId))
                    continue;

                try
                {
                    int width = graphics.Width;
                    int height = graphics.Height;
                    if (width is < 1 or > ConsoleContractLimits.MaxTooltipImageDimension ||
                        height is < 1 or > ConsoleContractLimits.MaxTooltipImageDimension)
                        throw new InvalidOperationException("dimensions");
                    byte[] png = EncodePng(graphics.Bitmap);
                    var resource = new ConsoleTooltipResource(graphicsId, png, width, height, revision);
                    long previousBytes = previous?.PngData.Count ?? 0;
                    long nextBytes = checked(projectedBytes - previousBytes + resource.PngData.Count);
                    if (nextBytes > structured.StateStore.Options.ContractLimits.MaxTooltipResourcesBytes ||
                        previous is null && projected.Count >= structured.StateStore.Options.ContractLimits.MaxTooltipResources)
                        throw new InvalidOperationException("budget");
                    operations.Add(ConsoleOperation.UpsertTooltipResource(resource));
                    projected[graphicsId] = resource;
                    projectedBytes = nextBytes;
                    dirtyTooltipGraphics.Remove(graphicsId);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException or IOException)
                {
                    RemoveUnavailableTooltipResource(
                        graphicsId,
                        previous,
                        projected,
                        operations,
                        ref projectedBytes,
                        revision,
                        exception.Message);
                }
            }

            ApplyTooltipProjectionOperations(structured, operations);
            tooltipProjectionImageMode = true;
        }
        finally
        {
            tooltipProjectionActive = false;
        }
    }

    private void RemoveUnavailableTooltipResource(
        int graphicsId,
        ConsoleTooltipResource? previous,
        Dictionary<int, ConsoleTooltipResource> projected,
        List<ConsoleOperation> operations,
        ref long projectedBytes,
        long revision,
        string reason)
    {
        if (previous is not null)
        {
            operations.Add(ConsoleOperation.RemoveTooltipResource(graphicsId));
            projected.Remove(graphicsId);
            projectedBytes -= previous.PngData.Count;
        }
        dirtyTooltipGraphics.Remove(graphicsId);
        string diagnostic = $"tooltip_graphics_unavailable:{graphicsId}:{revision}:{reason}";
        if (tooltipProjectionWarnings.Count < 256 && tooltipProjectionWarnings.Add(diagnostic))
            RecordWarning(diagnostic);
    }

    private static void ApplyTooltipProjectionOperations(
        StructuredGameConsole structured,
        IReadOnlyList<ConsoleOperation> operations)
    {
        int maximum = structured.StateStore.Options.ContractLimits.MaxTransactionOperations;
        for (int offset = 0; offset < operations.Count; offset += maximum)
        {
            ConsoleOperation[] batch = operations.Skip(offset).Take(maximum).ToArray();
            SequencedConsoleTransaction transaction = structured.EmitTransaction(new ConsoleTransaction(batch));
            RuntimeDebugTrace.Current?.RecordTransaction(transaction);
        }
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
            null,
            decorations,
            "session-default",
            Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize),
            Math.Max(MinorShift.Emuera.Runtime.Config.Config.FontSize, MinorShift.Emuera.Runtime.Config.Config.LineHeight),
            buttonColor: ToConsoleColor(stringStyle.ButtonColor));
    }

    private WindowMetadata CurrentWindowMetadata() => new(
        DisplayText(windowTitle),
        viewportWidth,
        viewportHeight,
        defaultBackground: ToConsoleColor(bgColor),
        defaultFont: new ConsoleFontSpec(
            "session-default",
            Math.Max(1, MinorShift.Emuera.Runtime.Config.Config.FontSize),
            Math.Max(MinorShift.Emuera.Runtime.Config.Config.FontSize, MinorShift.Emuera.Runtime.Config.Config.LineHeight)),
        fontFaceId: fontFaceId,
        webFontAssetDigest: webFontAssetDigest);

    private string DisplayText(string value) => HeadlessDisplayText.Project(value, convertBackslashToYen);

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

    private void RecordMessageAndQueue(string value)
    {
        RecordMessage(value);
        QueueDiagnosticLine(value);
    }

    private void RecordWarning(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            runtimeWarnings.Add(value.Trim());
    }

    private void QueueDiagnosticLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        string diagnostic = value.Trim();
        if (hasFatalError)
        {
            EmitDiagnosticLine(diagnostic);
            return;
        }

        pendingDiagnosticLines.Add(diagnostic);
    }

    private void FlushFatalDiagnosticLines()
    {
        foreach (string diagnostic in pendingDiagnosticLines)
            EmitDiagnosticLine(diagnostic);
        pendingDiagnosticLines.Clear();
    }

    private void EmitDiagnosticLine(string value)
    {
        if (outputEnabled)
            EmitLine($"⚠ {value}");
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

    public void Dispose() => stringMeasure?.Dispose();

    private static string FormatDiagnostic(string value, ScriptPosition? position) =>
        position is { } located
            ? $"{value} ({located.Filename}:{located.LineNo})"
            : value;

}
