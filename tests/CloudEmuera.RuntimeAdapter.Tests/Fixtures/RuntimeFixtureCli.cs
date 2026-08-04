namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

public static class RuntimeFixtureCli
{
    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        string? rootArgument = null;
        bool update = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++index]))
                    {
                        error.WriteLine("error: --root requires a path");
                        return 2;
                    }

                    rootArgument = args[index];
                    break;
                case "--update":
                    update = true;
                    break;
                case "--help":
                case "-h":
                    output.WriteLine("Usage: runtime-fixture-validator [--root <path>] [--update]");
                    output.WriteLine("Default mode validates local fixture bytes without changing files.");
                    return 0;
                default:
                    error.WriteLine($"error: unknown argument: {args[index]}");
                    return 2;
            }
        }

        string root;
        try
        {
            root = rootArgument is null ? RuntimeFixtureRepository.FindFixtureRoot() : Path.GetFullPath(rootArgument);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }

        if (update)
        {
            IReadOnlyList<string> updateErrors = RuntimeFixtureManifestUpdater.UpdateHashes(root);
            if (updateErrors.Count > 0)
            {
                WriteErrors(error, updateErrors, "fixture manifest update failed");
                return 1;
            }

            output.WriteLine("Updated fixture SHA-256 values.");
        }

        RuntimeFixtureValidationResult result = RuntimeFixtureValidator.Validate(root);
        if (!result.IsValid)
        {
            WriteErrors(error, result.Errors, "runtime fixture validation failed");
            return 1;
        }

        output.WriteLine(
            $"Runtime fixtures valid: schema {result.Manifest!.SchemaVersion}, {result.FixtureCount} fixture(s), {result.FileCount} file(s).");
        return 0;
    }

    private static void WriteErrors(TextWriter writer, IReadOnlyList<string> errors, string title)
    {
        writer.WriteLine(title + ":");
        foreach (string error in errors.OrderBy(error => error, StringComparer.Ordinal))
        {
            writer.WriteLine($"- {error}");
        }
    }
}
