using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Metrics.Api.Parsing;

public static partial class NoteParser
{
    [GeneratedRegex(@"^(\d+)(?:-(\d+))?$")]
    private static partial Regex RangeToken();

    public static ParsedNote Parse(string noteContent)
    {
        if (string.IsNullOrWhiteSpace(noteContent))
            throw new FormatException("note_content is empty");
        if (!noteContent.Contains("---", StringComparison.Ordinal))
            throw new FormatException("note missing '---' separator between file map and JSON");

        // Split on a line containing only '---'. Tolerate leading separator (pure human commits).
        var idx = noteContent.IndexOf("---", StringComparison.Ordinal);
        var head = noteContent[..idx];
        var tail = noteContent[(idx + 3)..].TrimStart('\r', '\n').TrimEnd();

        var parsed = new ParsedNote();
        string? currentFile = null;
        foreach (var rawLine in head.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (currentFile is null)
                    throw new FormatException($"indented line before any filename: {line}");
                var trimmed = line.Trim();
                var spaceIdx = trimmed.IndexOf(' ');
                if (spaceIdx <= 0)
                    throw new FormatException($"malformed file map line: {line}");
                var attrId = trimmed[..spaceIdx];
                var ranges = trimmed[(spaceIdx + 1)..].Trim();
                parsed.Entries.Add(new FileMapEntry(currentFile, attrId, CountLines(ranges)));
            }
            else
            {
                currentFile = line.Trim();
            }
        }

        if (tail.Length > 0)
        {
            using var doc = JsonDocument.Parse(tail);
            var root = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            if (root["prompts"] is JsonObject prompts)
                foreach (var kv in prompts)
                    if (kv.Value is JsonObject obj) parsed.Prompts[kv.Key] = obj;
            if (root["humans"] is JsonObject humans)
                foreach (var kv in humans)
                    if (kv.Value is JsonObject obj) parsed.Humans[kv.Key] = obj;
        }

        return parsed;
    }

    private static int CountLines(string ranges)
    {
        var total = 0;
        foreach (var rawTok in ranges.Split(','))
        {
            var tok = rawTok.Trim();
            if (tok.Length == 0) continue;
            var m = RangeToken().Match(tok);
            if (!m.Success)
                throw new FormatException($"bad range token: {tok}");
            var start = int.Parse(m.Groups[1].Value);
            var end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : start;
            if (end < start)
                throw new FormatException($"inverted range: {tok}");
            total += end - start + 1;
        }
        return total;
    }
}
