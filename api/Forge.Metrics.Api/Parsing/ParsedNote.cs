using System.Text.Json.Nodes;

namespace Forge.Metrics.Api.Parsing;

public sealed record FileMapEntry(string File, string AttributionId, int LineCount)
{
    public bool IsHuman => AttributionId.StartsWith("h_", StringComparison.Ordinal);
}

public sealed class ParsedNote
{
    public List<FileMapEntry> Entries { get; } = new();
    public Dictionary<string, JsonObject> Prompts { get; } = new();
    public Dictionary<string, JsonObject> Humans { get; } = new();
}
