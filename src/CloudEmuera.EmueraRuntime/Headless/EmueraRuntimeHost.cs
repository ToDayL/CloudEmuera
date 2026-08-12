using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

public sealed class EmueraRuntimeHost : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DeadlineCancellationGrace = TimeSpan.FromSeconds(1);
    private const int MaxInitializationWarnings = 128;
    private static readonly HashSet<string> UnsupportedHeadlessIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALLSHARP", "GETKEY", "GETKEYTRIGGERED", "MOUSEX", "MOUSEY", "MOUSEB", "GETTEXTBOX", "SETTEXTBOX",
    };
    private readonly object sync = new();
    private readonly EmueraRuntimeOptions options;
    private readonly List<EmueraRuntimeDiagnostic> diagnostics = [];
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private InitializedRuntime? initializedRuntime;
    private ReadOnlyDictionary<string, SpriteDefinition> sprites =
        new ReadOnlyDictionary<string, SpriteDefinition>(new Dictionary<string, SpriteDefinition>());
    private HostState state;

    private EmueraRuntimeHost(EmueraRuntimeOptions options)
    {
        this.options = options;
    }

    public static EmueraRuntimeHost Create(EmueraRuntimeOptions options) => new(options);

    public bool IsInitialized
    {
        get
        {
            lock (sync)
            {
                return state is HostState.Initialized or HostState.Running or HostState.Completed;
            }
        }
    }

    public async Task<EmueraRuntimeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (state != HostState.Created)
            {
                throw new InvalidOperationException("This runtime host has already been initialized or disposed.");
            }

            state = HostState.Initializing;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
        try
        {
            InitializedRuntime loaded = await RunWithDeadlineAsync(
                InitializeUpstreamRuntime,
                options.InitializationDeadline,
                linked,
                EmueraRuntimeStatus.DeadlineExceeded,
                static lateRuntime => lateRuntime.Dispose()).ConfigureAwait(false);
            lock (sync)
            {
                initializedRuntime = loaded;
                state = HostState.Initialized;
            }

            foreach (string warning in loaded.Session.InitializationWarnings.Take(MaxInitializationWarnings))
            {
                AddDiagnostic("runtime_warning", EmueraRuntimePhase.Initialization, warning, fatal: false);
            }
            return Result(EmueraRuntimeStatus.Completed);
        }
        catch (RuntimeDeadlineException)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("runtime_initialization_deadline", EmueraRuntimePhase.Initialization, "Runtime initialization exceeded its deadline.", true);
            return Result(EmueraRuntimeStatus.DeadlineExceeded);
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            return Result(EmueraRuntimeStatus.Cancelled);
        }
        catch (NotSupportedException exception)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("unsupported_runtime_capability", EmueraRuntimePhase.Loading, exception.Message, true);
            return Result(EmueraRuntimeStatus.UnsupportedCapability);
        }
        catch (UpstreamSaveLayoutMismatchException exception)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("save_layout_mismatch", EmueraRuntimePhase.Initialization, exception.Message, true);
            return Result(EmueraRuntimeStatus.InitializationFailed);
        }
        catch (UpstreamSaveLayoutConflictException exception)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("save_layout_conflict", EmueraRuntimePhase.Initialization, exception.Message, true);
            return Result(EmueraRuntimeStatus.InitializationFailed);
        }
        catch (RuntimeSaveLayoutInspectionException exception)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("save_layout_invalid", EmueraRuntimePhase.Initialization, exception.Message, true);
            return Result(EmueraRuntimeStatus.InitializationFailed);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or RuntimeFileAccessException)
        {
            lock (sync)
            {
                state = HostState.Failed;
            }

            AddDiagnostic("runtime_initialization_failed", EmueraRuntimePhase.Initialization, SafeMessage(exception), true);
            return Result(EmueraRuntimeStatus.InitializationFailed);
        }
    }

    public async Task<EmueraRuntimeResult> RunAsync(CancellationToken cancellationToken = default)
    {
        UpstreamRuntimeSession loaded;
        lock (sync)
        {
            if (state != HostState.Initialized || initializedRuntime is null)
            {
                throw new InvalidOperationException("The runtime must be initialized exactly once before it can run.");
            }

            state = HostState.Running;
            loaded = initializedRuntime.Session;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
        try
        {
            await RunWithDeadlineAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    loaded.Run(token);
                    return true;
                },
                options.RunDeadline,
                linked,
                EmueraRuntimeStatus.DeadlineExceeded).ConfigureAwait(false);
            lock (sync)
            {
                state = HostState.Completed;
            }

            return Result(EmueraRuntimeStatus.Completed);
        }
        catch (RuntimeDeadlineException)
        {
            lock (sync)
            {
                state = HostState.Completed;
            }

            AddDiagnostic("runtime_execution_deadline", EmueraRuntimePhase.Execution, "Runtime execution exceeded its deadline.", true);
            return Result(EmueraRuntimeStatus.DeadlineExceeded);
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                state = HostState.Completed;
            }

            return Result(EmueraRuntimeStatus.Cancelled);
        }
        catch (InvalidDataException exception)
        {
            lock (sync)
            {
                state = HostState.Completed;
            }

            AddDiagnostic("runtime_script_failed", EmueraRuntimePhase.Execution, exception.Message, true);
            return Result(EmueraRuntimeStatus.ScriptFailed);
        }
        catch (NotSupportedException exception)
        {
            lock (sync)
            {
                state = HostState.Completed;
            }

            AddDiagnostic("unsupported_runtime_capability", EmueraRuntimePhase.Media, exception.Message, true);
            return Result(EmueraRuntimeStatus.UnsupportedCapability);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (state == HostState.Disposed)
            {
                return;
            }

            state = HostState.Disposed;
        }

        lifetimeCancellation.Cancel();
        initializedRuntime?.Dispose();
        lifetimeCancellation.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private InitializedRuntime InitializeUpstreamRuntime(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        RuntimeFilePath configuration = new(RuntimeFileArea.Configuration, "emuera.config");
        if (!options.FileSystem.FileExists(configuration, cancellationToken))
        {
            throw new FileNotFoundException("The controlled runtime configuration is missing.");
        }

        using (Stream configurationStream = options.FileSystem.OpenRead(configuration, cancellationToken))
        {
            RuntimeSaveLayout actualLayout = EmueraSaveLayoutInspector.Inspect(configurationStream);
            if (actualLayout != options.Paths.SaveLayout)
            {
                throw new UpstreamSaveLayoutMismatchException(options.Paths.SaveLayout, actualLayout);
            }
        }

        RuntimeFilePath gameBase = new(RuntimeFileArea.GameContent, "CSV/GAMEBASE.CSV");
        if (!options.FileSystem.FileExists(gameBase, cancellationToken))
        {
            throw new FileNotFoundException("CSV/GAMEBASE.CSV is missing.");
        }

        string gameBaseText = ReadText(gameBase, SelectEncoding(), cancellationToken);
        if (string.IsNullOrWhiteSpace(gameBaseText))
        {
            throw new InvalidDataException("CSV/GAMEBASE.CSV is empty.");
        }

        RuntimeFilePath erbDirectory = new(RuntimeFileArea.GameContent, "ERB");
        if (!options.FileSystem.DirectoryExists(erbDirectory, cancellationToken))
        {
            throw new DirectoryNotFoundException("The ERB directory is missing.");
        }

        IReadOnlyList<RuntimeFileEntry> entries = options.FileSystem.Enumerate(erbDirectory, cancellationToken);
        foreach (RuntimeFileEntry entry in entries
            .Where(entry => entry.Kind == RuntimeFileEntryKind.File && entry.Path.LogicalPath.EndsWith(".ERB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path.LogicalPath, StringComparer.Ordinal))
        {
            ValidateNoUnsupportedCapabilities(ReadText(entry.Path, SelectEncoding(), cancellationToken));
        }

        if (!entries.Any(entry => entry.Kind == RuntimeFileEntryKind.File && entry.Path.LogicalPath.EndsWith(".ERB", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException("No ERB source files were found.");
        }

        sprites = LoadSprites(cancellationToken);
        UpstreamRuntimeSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = new UpstreamRuntimeSession(
                options.Console,
                options.Clock,
                options.AudioPort,
                cancellationToken,
                name => sprites.TryGetValue(name, out SpriteDefinition? sprite)
                    ? new RuntimeSpriteDefinition(
                        sprite.AssetId,
                        sprite.SourceX,
                        sprite.SourceY,
                        sprite.SourceWidth,
                        sprite.SourceHeight,
                        sprite.DestinationOffsetX,
                        sprite.DestinationOffsetY,
                        sprite.DestinationWidth,
                        sprite.DestinationHeight,
                        sprite.AnimationFrames)
                    : null,
                options.UpstreamGateAcquired);
            bool initialized = session.InitializeAsync(options.Paths).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialized)
            {
                string details = string.Join(" | ", session.InitializationMessages.Take(8));
                throw new InvalidDataException($"The pinned upstream Emuera loader rejected the controlled game content. {details}");
            }

            return new InitializedRuntime(session);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    private static void ValidateNoUnsupportedCapabilities(string source)
    {
        foreach (string line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (line.TrimStart().StartsWith("CALLSHARP", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("CALLSHARP is unavailable in the headless runtime.");
            foreach ((string identifier, int end) in EnumerateIdentifiers(line))
            {
                int next = end;
                while (next < line.Length && char.IsWhiteSpace(line[next]))
                    next++;
                bool invocation = next < line.Length && line[next] == '(';
                if (invocation && UnsupportedHeadlessIdentifiers.Contains(identifier))
                {
                    throw new NotSupportedException($"{identifier} is unavailable in the headless runtime.");
                }
            }
        }
    }

    private static IEnumerable<(string Identifier, int End)> EnumerateIdentifiers(string line)
    {
        bool quoted = false;
        for (int index = 0; index < line.Length;)
        {
            char current = line[index];
            if (current == '"')
            {
                quoted = !quoted;
                index++;
                continue;
            }
            if (!quoted && current == ';')
                yield break;
            if (!quoted && (char.IsLetter(current) || current == '_'))
            {
                int start = index++;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
                    index++;
                yield return (line[start..index], index);
                continue;
            }
            index++;
        }
    }

    private ReadOnlyDictionary<string, SpriteDefinition> LoadSprites(CancellationToken cancellationToken)
    {
        RuntimeFilePath resources = new(RuntimeFileArea.GameContent, "resources");
        if (!options.FileSystem.DirectoryExists(resources, cancellationToken))
        {
            return new ReadOnlyDictionary<string, SpriteDefinition>(new Dictionary<string, SpriteDefinition>());
        }

        var result = new Dictionary<string, SpriteDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeFilePath spriteCsv in EnumerateResourceCsvFiles(resources, cancellationToken))
        {
            string? currentAnimationName = null;
            string relative = spriteCsv.RelativePath.Value;
            string directory = relative.Contains('/')
                ? relative[..(relative.LastIndexOf('/') + 1)]
                : "resources/";
            string content = ReadText(spriteCsv, SelectEncoding(), cancellationToken);
            foreach (string rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(';'))
                    continue;

                string[] fields = trimmed.Split(',');
                if (fields.Length < 2)
                    continue;
                string name = fields[0].Trim().ToUpperInvariant();
                string filename = fields[1].Trim();
                if (filename.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentAnimationName is not null && result[currentAnimationName].AnimationFrames.Length == 0)
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an animated Sprite without frames.");
                    if (fields.Length < 4 || !TryInt(fields[2], out int animeWidth) || !TryInt(fields[3], out int animeHeight) ||
                        animeWidth <= 0 || animeHeight <= 0 || animeWidth > 8_192 || animeHeight > 8_192)
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an invalid animated Sprite declaration.");
                    if (!result.TryAdd(name, new SpriteDefinition(
                        name, string.Empty, null, 0, 0, animeWidth, animeHeight, 0, 0, animeWidth, animeHeight, [])))
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains a duplicate Sprite name.");
                    currentAnimationName = name;
                    continue;
                }
                if (name.Length == 0 || filename.Length == 0)
                    continue;

                RuntimeFilePath imagePath = new(RuntimeFileArea.GameContent, directory + filename);
                RuntimeImageMetadata metadata = options.ImagePort.Load(imagePath, cancellationToken);
                int x = 0;
                int y = 0;
                int width = metadata.Width;
                int height = metadata.Height;
                if (fields.Length >= 6 &&
                    (!TryInt(fields[2], out x) || !TryInt(fields[3], out y) ||
                     !TryInt(fields[4], out width) || !TryInt(fields[5], out height)))
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an invalid Sprite rectangle.");
                if (width <= 0 || height <= 0 || x < 0 || y < 0 || x > metadata.Width - width || y > metadata.Height - height)
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an out-of-bounds Sprite rectangle.");

                int offsetX = 0;
                int offsetY = 0;
                if (fields.Length >= 8 && (!TryInt(fields[6], out offsetX) || !TryInt(fields[7], out offsetY)))
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an invalid Sprite offset.");
                int destinationWidth = width;
                int destinationHeight = height;
                if (fields.Length >= 11 && (!TryInt(fields[9], out destinationWidth) || !TryInt(fields[10], out destinationHeight)))
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an invalid Sprite destination size.");
                if (destinationWidth <= 0 || destinationHeight <= 0)
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains a non-positive Sprite destination size.");

                if (currentAnimationName is not null && name.Equals(currentAnimationName, StringComparison.OrdinalIgnoreCase))
                {
                    int delay = 1_000;
                    if (fields.Length >= 9 && (!TryInt(fields[8], out delay) || delay <= 0))
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an invalid animated Sprite delay.");
                    SpriteDefinition animation = result[currentAnimationName];
                    if (animation.AnimationFrames.Length >= ConsoleContractLimits.Default.MaxSpriteFrames)
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains too many animated Sprite frames.");
                    int clippedLeft = Math.Max(0, offsetX);
                    int clippedTop = Math.Max(0, offsetY);
                    int clippedRight = Math.Min(animation.DestinationWidth, checked(offsetX + width));
                    int clippedBottom = Math.Min(animation.DestinationHeight, checked(offsetY + height));
                    if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
                        throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an animated Sprite frame outside its canvas.");
                    int clippedSourceX = checked(x + clippedLeft - offsetX);
                    int clippedSourceY = checked(y + clippedTop - offsetY);
                    int clippedWidth = clippedRight - clippedLeft;
                    int clippedHeight = clippedBottom - clippedTop;
                    result[currentAnimationName] = animation with
                    {
                        AssetId = animation.AssetId.Length == 0 ? metadata.ResourceId : animation.AssetId,
                        Path = animation.AssetId.Length == 0 ? imagePath : animation.Path,
                        SourceX = animation.AssetId.Length == 0 ? x : animation.SourceX,
                        SourceY = animation.AssetId.Length == 0 ? y : animation.SourceY,
                        SourceWidth = animation.AssetId.Length == 0 ? width : animation.SourceWidth,
                        SourceHeight = animation.AssetId.Length == 0 ? height : animation.SourceHeight,
                        AnimationFrames = animation.AnimationFrames.Append(new RuntimeSpriteFrame(
                            metadata.ResourceId, clippedSourceX, clippedSourceY, clippedWidth, clippedHeight,
                            clippedLeft, clippedTop, delay)).ToArray()
                    };
                    continue;
                }

                if (currentAnimationName is not null && result[currentAnimationName].AnimationFrames.Length == 0)
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an animated Sprite without frames.");
                currentAnimationName = null;

                if (!result.TryAdd(name, new SpriteDefinition(
                    name,
                    metadata.ResourceId,
                    imagePath,
                    x,
                    y,
                    width,
                    height,
                    offsetX,
                    offsetY,
                    destinationWidth,
                    destinationHeight,
                    [])))
                    throw new InvalidDataException($"{spriteCsv.LogicalPath} contains a duplicate Sprite name.");
            }
            if (currentAnimationName is not null && result[currentAnimationName].AnimationFrames.Length == 0)
                throw new InvalidDataException($"{spriteCsv.LogicalPath} contains an animated Sprite without frames.");
        }

        return new ReadOnlyDictionary<string, SpriteDefinition>(result);
    }

    private List<RuntimeFilePath> EnumerateResourceCsvFiles(RuntimeFilePath root, CancellationToken cancellationToken)
    {
        var result = new List<RuntimeFilePath>();
        var pending = new Queue<RuntimeFilePath>();
        pending.Enqueue(root);
        while (pending.Count != 0)
        {
            RuntimeFilePath directory = pending.Dequeue();
            foreach (RuntimeFileEntry entry in options.FileSystem.Enumerate(directory, cancellationToken))
            {
                if (entry.Kind == RuntimeFileEntryKind.Directory)
                    pending.Enqueue(entry.Path);
                else if (entry.Path.LogicalPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    result.Add(entry.Path);
            }
        }
        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));
        return result;
    }

    private static bool TryInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private string ReadText(RuntimeFilePath path, Encoding encoding, CancellationToken cancellationToken)
    {
        using Stream stream = options.FileSystem.OpenRead(path, cancellationToken);
        using var reader = new StreamReader(stream, encoding, true, 4096, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private Encoding SelectEncoding() => options.CompatibilityProfile == EmueraCompatibilityProfiles.V18Compatible
        ? Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        : new UTF8Encoding(false, true);

    private async Task<T> RunWithDeadlineAsync<T>(
        Func<CancellationToken, T> operation,
        TimeSpan deadline,
        CancellationTokenSource linked,
        EmueraRuntimeStatus deadlineStatus,
        Action<T>? lateResultCleanup = null)
    {
        Task<T> work = Task.Run(() => operation(linked.Token), CancellationToken.None);
        if (deadline == Timeout.InfiniteTimeSpan)
        {
            return await work.ConfigureAwait(false);
        }

        Task delay = options.Clock.DelayAsync(deadline, linked.Token).AsTask();
        Task winner = await Task.WhenAny(work, delay).ConfigureAwait(false);
        if (winner == work)
        {
            linked.Cancel();
            return await work.ConfigureAwait(false);
        }

        bool wasCancelledByCaller = linked.IsCancellationRequested;
        linked.Cancel();
        try
        {
            T lateResult = await work.WaitAsync(DeadlineCancellationGrace).ConfigureAwait(false);
            TryCleanupLateResult(lateResultCleanup, lateResult);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            _ = work.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        TryCleanupLateResult(lateResultCleanup, completed.Result);
                    else if (completed.IsFaulted)
                        _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (wasCancelledByCaller)
            throw new OperationCanceledException(linked.Token);
        throw new RuntimeDeadlineException(deadlineStatus);
    }

    private static void TryCleanupLateResult<T>(Action<T>? cleanup, T result)
    {
        if (cleanup is null)
            return;
        try
        {
            cleanup(result);
        }
        catch
        {
            // The deadline result has already won. Cleanup must not replace it
            // with an unrelated continuation exception.
        }
    }

    private void AddDiagnostic(
        string code,
        EmueraRuntimePhase phase,
        string message,
        bool fatal,
        string? sourcePath = null,
        int? lineNumber = null)
    {
        var diagnostic = new EmueraRuntimeDiagnostic(code, phase, message, fatal, sourcePath, lineNumber);
        diagnostics.Add(diagnostic);
        options.DiagnosticSink?.Invoke(diagnostic);
    }

    private EmueraRuntimeResult Result(
        EmueraRuntimeStatus status,
        IReadOnlyDictionary<string, string>? variables = null) =>
        new(status, diagnostics.ToArray(), variables);

    private string SafeMessage(Exception exception)
    {
        string message = exception switch
        {
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException => exception.Message,
            RuntimeFileAccessException => "A controlled runtime file operation was rejected.",
            _ => "Runtime initialization failed."
        };
        foreach (string root in new[]
        {
            options.Paths.GameContentRoot,
            options.Paths.SessionRoot,
            options.Paths.SessionWorkspaceRoot,
            options.Paths.TemporaryRoot
        }.OrderByDescending(value => value.Length))
        {
            message = message.Replace(root, "<runtime-path>", StringComparison.Ordinal);
        }

        return message;
    }

    private enum HostState
    {
        Created,
        Initializing,
        Initialized,
        Running,
        Completed,
        Failed,
        Disposed
    }

    private sealed record SpriteDefinition(
        string Name,
        string AssetId,
        RuntimeFilePath? Path,
        int SourceX,
        int SourceY,
        int SourceWidth,
        int SourceHeight,
        int DestinationOffsetX,
        int DestinationOffsetY,
        int DestinationWidth,
        int DestinationHeight,
        RuntimeSpriteFrame[] AnimationFrames);

    private sealed class InitializedRuntime(UpstreamRuntimeSession session) : IDisposable
    {
        private int disposed;

        public UpstreamRuntimeSession Session { get; } = session;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            Session.Dispose();
        }
    }

    private sealed class RuntimeDeadlineException(EmueraRuntimeStatus status) : Exception
    {
        public EmueraRuntimeStatus Status { get; } = status;
    }

}
