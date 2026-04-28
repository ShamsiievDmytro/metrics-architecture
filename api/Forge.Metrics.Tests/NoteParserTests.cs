using Forge.Metrics.Api.Parsing;
using Xunit;

namespace Forge.Metrics.Tests;

public class NoteParserTests
{
    private const string MixedNote = """
calc-test-ai.json
  h_dca485b1adf836 11-13
  33d7da781a966cb5 9-10
human-only.json
  h_dca485b1adf836 1-3
---
{
  "prompts": {
    "33d7da781a966cb5": {
      "agent": "claude",
      "model": "claude-opus-4-7",
      "accepted": true,
      "overridden_lines": 1
    }
  },
  "humans": {
    "h_dca485b1adf836": { "author": "Dmytro Shamsiiev" }
  }
}
""";

    private const string PureHumanNote = """
---
{"prompts": {}, "humans": {"h_aaa": {"author": "Bob"}}}
""";

    private const string MultiRangeNote = """
foo.py
  33d7da781a966cb5 1-2,5,7-9
---
{"prompts": {"33d7da781a966cb5": {"agent": "claude", "model": "x", "accepted": true, "overridden_lines": 0}}, "humans": {}}
""";

    [Fact]
    public void Parses_mixed_note_filemap_entries()
    {
        var parsed = NoteParser.Parse(MixedNote);
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal(new FileMapEntry("calc-test-ai.json", "h_dca485b1adf836", 3), parsed.Entries[0]);
        Assert.Equal(new FileMapEntry("calc-test-ai.json", "33d7da781a966cb5", 2), parsed.Entries[1]);
        Assert.Equal(new FileMapEntry("human-only.json", "h_dca485b1adf836", 3), parsed.Entries[2]);
    }

    [Fact]
    public void Parses_mixed_note_json_metadata()
    {
        var parsed = NoteParser.Parse(MixedNote);
        Assert.Contains("33d7da781a966cb5", parsed.Prompts.Keys);
        Assert.Equal("claude", parsed.Prompts["33d7da781a966cb5"]["agent"]!.GetValue<string>());
        Assert.Equal(1, parsed.Prompts["33d7da781a966cb5"]["overridden_lines"]!.GetValue<int>());
        Assert.Equal("Dmytro Shamsiiev", parsed.Humans["h_dca485b1adf836"]["author"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_pure_human_note_with_empty_filemap()
    {
        var parsed = NoteParser.Parse(PureHumanNote);
        Assert.Empty(parsed.Entries);
        Assert.Empty(parsed.Prompts);
        Assert.Equal("Bob", parsed.Humans["h_aaa"]["author"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_multiple_ranges_summing_lines()
    {
        var parsed = NoteParser.Parse(MultiRangeNote);
        // 1-2 (2) + 5 (1) + 7-9 (3) = 6
        Assert.Equal(6, parsed.Entries[0].LineCount);
    }

    [Fact]
    public void Throws_when_separator_missing()
    {
        Assert.Throws<FormatException>(() => NoteParser.Parse("calc.json\n  h_a 1-2\n"));
    }
}
