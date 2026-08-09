using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

public sealed class EmueraRuntimeHost : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DeadlineCancellationGrace = TimeSpan.FromSeconds(1);
    private static readonly HashSet<string> UnsupportedHeadlessIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALLSHARP", "GETKEY", "GETKEYTRIGGERED", "MOUSEX", "MOUSEY", "MOUSEB", "GETTEXTBOX", "SETTEXTBOX",
        "GCREATED", "GWIDTH", "GHEIGHT", "GGETCOLOR", "GCREATE", "GCREATEFROMFILE", "GDISPOSE", "GCLEAR",
        "GFILLRECTANGLE", "GDRAWSPRITE", "GSETCOLOR", "GDRAWG", "GDRAWGWITHMASK", "GSETBRUSH", "GSETFONT",
        "GSETPEN", "GSAVE", "GLOAD", "GDRAWGWITHROTATE", "GDRAWTEXT", "GGETFONT", "GGETFONTSIZE",
        "GGETFONTSTYLE", "GGETTEXTSIZE", "GGETBRUSH", "GGETPEN", "GGETPENWIDTH", "GDRAWLINE", "GDASHSTYLE",
        "SPRITEGETCOLOR", "SPRITECREATE", "SPRITEDISPOSE", "SPRITEANIMECREATE", "SPRITEANIMEADDFRAME",
        "SPRITEDISPOSEALL"
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
                    ? (sprite.Name, sprite.Width, sprite.Height)
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
                if (invocation && (identifier.StartsWith("CBG", StringComparison.OrdinalIgnoreCase) ||
                    UnsupportedHeadlessIdentifiers.Contains(identifier)))
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
        RuntimeFilePath spriteCsv = new(RuntimeFileArea.GameContent, "resources/sprites.csv");
        if (!options.FileSystem.FileExists(spriteCsv, cancellationToken))
        {
            return new ReadOnlyDictionary<string, SpriteDefinition>(new Dictionary<string, SpriteDefinition>());
        }

        var result = new Dictionary<string, SpriteDefinition>(StringComparer.OrdinalIgnoreCase);
        string content = ReadText(spriteCsv, SelectEncoding(), cancellationToken);
        foreach (string rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            string[] fields = rawLine.Split(',');
            if (fields.Length != 6 ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int height) || width <= 0 || height <= 0)
            {
                throw new InvalidDataException("resources/sprites.csv contains an invalid sprite row.");
            }

            string name = fields[0].Trim();
            string filename = fields[1].Trim();
            RuntimeFilePath imagePath = new(RuntimeFileArea.GameContent, $"resources/{filename}");
            RuntimeImageMetadata metadata = options.ImagePort.Load(imagePath, cancellationToken);
            if (x < 0 || y < 0 || x > metadata.Width - width || y > metadata.Height - height)
            {
                throw new InvalidDataException("A sprite rectangle is outside its source image.");
            }

            if (!result.TryAdd(name, new SpriteDefinition(name, imagePath, width, height)))
            {
                throw new InvalidDataException("resources/sprites.csv contains a duplicate sprite name.");
            }
        }

        return new ReadOnlyDictionary<string, SpriteDefinition>(result);
    }

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

    private sealed record SpriteDefinition(string Name, RuntimeFilePath Path, int Width, int Height);

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
