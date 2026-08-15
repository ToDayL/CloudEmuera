using System.Text.Json;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

public sealed class SharedReducerGoldenTests
{
    [Fact]
    [Trait("Category", "Realtime")]
    public void CSharpReducerMatchesTheSameBrowserFixture()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(FindFixture()));
        JsonElement root = fixture.RootElement;
        JsonElement initial = root.GetProperty("initialState");
        var baseline = new ConsoleSnapshot(
            0,
            initial.GetProperty("lines").EnumerateArray().Select(ParseLine));

        var transactions = root.GetProperty("transactions").EnumerateArray()
            .Select(transaction => new SequencedConsoleTransaction(
                transaction.GetProperty("sequence").GetInt64(),
                new ConsoleTransaction(transaction.GetProperty("operations").EnumerateArray().Select(ParseOperation))))
            .ToArray();

        ConsoleSnapshot actual = ConsoleSnapshotReducer.ApplyBatch(baseline, transactions, ConsoleHistoryOptions.Default);
        JsonElement expected = root.GetProperty("expectedState");
        Assert.Equal(expected.GetProperty("sequence").GetInt64(), actual.SnapshotSequence);
        var actualLines = actual.Scrollback.Select(line => new GoldenLine(
            line.LineId,
            string.Concat(line.Nodes.OfType<TextNode>().Select(node => node.Text)),
            line.Alignment.ToString().ToLowerInvariant(),
            line.Temporary)).ToArray();
        GoldenLine[] expectedLines = expected.GetProperty("lines").EnumerateArray().Select(ParseExpectedLine).ToArray();
        Assert.Equal(expectedLines, actualLines);
    }

    private static ConsoleLine ParseLine(JsonElement value) => new(
        value.GetProperty("lineId").GetString()!,
        [new TextNode(value.GetProperty("text").GetString()!)],
        ParseAlignment(value.GetProperty("alignment").GetString()!),
        value.GetProperty("temporary").GetBoolean());

    private static ConsoleOperation ParseOperation(JsonElement value) => value.GetProperty("type").GetString() switch
    {
        "appendLine" => ConsoleOperation.AppendLine(ParseLine(value.GetProperty("line"))),
        "replaceLine" => ConsoleOperation.ReplaceLine(ParseLine(value.GetProperty("line"))),
        "appendInline" => ConsoleOperation.AppendInline(value.GetProperty("lineId").GetString()!, [new TextNode(value.GetProperty("text").GetString()!)]),
        "deleteLines" => ConsoleOperation.DeleteLines(value.GetProperty("lineIds").EnumerateArray().Select(item => item.GetString()!)),
        string type => throw new InvalidDataException($"Unknown shared reducer operation {type}.") ,
        null => throw new InvalidDataException("Shared reducer operation type is missing."),
    };

    private static GoldenLine ParseExpectedLine(JsonElement value) => new(
        value.GetProperty("lineId").GetString()!,
        value.GetProperty("text").GetString()!,
        value.GetProperty("alignment").GetString()!,
        value.GetProperty("temporary").GetBoolean());

    private static ConsoleLineAlignment ParseAlignment(string value) => value switch
    {
        "left" => ConsoleLineAlignment.Left,
        "center" => ConsoleLineAlignment.Center,
        "right" => ConsoleLineAlignment.Right,
        _ => throw new InvalidDataException($"Unknown shared reducer alignment {value}."),
    };

    private static string FindFixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "CloudEmuera.Web", "src", "realtime", "fixtures", "reducer-v1.json");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("The shared reducer golden fixture was not found.");
    }

    private sealed record GoldenLine(string LineId, string Text, string Alignment, bool Temporary);
}
