using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudEmuera.RuntimeAdapter.Tests.Fixtures;

internal sealed class TemporaryFixtureCopy : IDisposable
{
    public TemporaryFixtureCopy()
    {
        Root = Directory.CreateTempSubdirectory("cloudemuera-runtime-fixture-").FullName;
        CopyDirectory(RuntimeFixtureRepository.FindFixtureRoot(), Root);
    }

    public string Root { get; }

    public JsonObject ReadManifest()
    {
        return JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "manifest.json"), Encoding.UTF8)) as JsonObject
            ?? throw new InvalidOperationException("Fixture manifest root is not an object.");
    }

    public void WriteManifest(JsonObject manifest)
    {
        File.WriteAllText(
            Path.Combine(Root, "manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    public static JsonObject GetFixture(JsonObject manifest, string id)
    {
        if (manifest["fixtures"] is not JsonArray fixtures)
        {
            throw new InvalidOperationException("Fixture manifest has no fixtures array.");
        }

        foreach (JsonNode? fixtureNode in fixtures)
        {
            if (fixtureNode is JsonObject fixture &&
                string.Equals(fixture["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
            {
                return fixture;
            }
        }

        throw new InvalidOperationException($"Fixture not found: {id}");
    }

    public static JsonObject GetFile(JsonObject fixture, string path)
    {
        if (fixture["files"] is not JsonArray files)
        {
            throw new InvalidOperationException("Fixture has no files array.");
        }

        foreach (JsonNode? fileNode in files)
        {
            if (fileNode is JsonObject file &&
                string.Equals(file["path"]?.GetValue<string>(), path, StringComparison.Ordinal))
            {
                return file;
            }
        }

        throw new InvalidOperationException($"Fixture file not found: {path}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(targetDirectory);
            CopyDirectory(directory, targetDirectory);
        }

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}
