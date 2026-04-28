using Forge.Metrics.Api.Parsing;
using Xunit;

namespace Forge.Metrics.Tests;

public class AttributionCalculatorTests
{
    private const string Mixed = """
calc-test-ai.json
  h_dca485b1adf836 11-13
  33d7da781a966cb5 9-10
human-only.json
  h_dca485b1adf836 1-3
---
{"prompts": {"33d7da781a966cb5": {"agent":"claude","model":"claude-opus-4-7","accepted":true,"overridden_lines":1}},
 "humans": {"h_dca485b1adf836":{"author":"Dmytro Shamsiiev"}}}
""";

    private const string Sibling = """
calc-test-sibling.json
  h_dca485b1adf836 7-8
  33d7da781a966cb5 5-6
---
{"prompts": {"33d7da781a966cb5": {"agent":"claude","model":"x","accepted":true,"overridden_lines":1}},
 "humans": {"h_dca485b1adf836":{"author":"X"}}}
""";

    private const string PureHuman = """
---
{"prompts": {}, "humans": {"h_a": {"author": "Bob"}}}
""";

    private const string MultiAgent = """
foo.py
  prompt_claude 1-3
  prompt_copilot 4-5
---
{"prompts": {
  "prompt_claude": {"agent":"claude","model":"opus","accepted":true,"overridden_lines":0},
  "prompt_copilot": {"agent":"github-copilot","model":"gpt-5","accepted":true,"overridden_lines":0}
 }, "humans": {}}
""";

    [Fact]
    public void Mixed_commit_is_25_percent()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Mixed));
        Assert.Equal(2, r.AgentLines);
        Assert.Equal(6, r.HumanLines);
        Assert.Equal(25.0m, r.AgentPercentage);
        Assert.Equal(1, r.OverriddenLines);
    }

    [Fact]
    public void Sibling_commit_is_50_percent()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Sibling));
        Assert.Equal(2, r.AgentLines);
        Assert.Equal(2, r.HumanLines);
        Assert.Equal(50.0m, r.AgentPercentage);
    }

    [Fact]
    public void Pure_human_with_no_filemap_returns_zeros()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(PureHuman));
        Assert.Equal(0, r.AgentLines);
        Assert.Equal(0, r.HumanLines);
        Assert.Equal(0m, r.AgentPercentage);
    }

    [Fact]
    public void Pure_human_with_diff_additions_fills_human_lines()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(PureHuman), enrichedDiffAdditions: 12);
        Assert.Equal(0, r.AgentLines);
        Assert.Equal(12, r.HumanLines);
        Assert.Equal(0m, r.AgentPercentage);
    }

    [Fact]
    public void Multi_agent_records_comma_separated_agents()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(MultiAgent));
        Assert.Equal(5, r.AgentLines);
        Assert.Equal(0, r.HumanLines);
        Assert.Equal(100m, r.AgentPercentage);
        Assert.Equal("claude,github-copilot", r.Agents);
        Assert.Equal("opus,gpt-5", r.Models);
    }

    [Fact]
    public void Single_agent_returns_single_agent_string()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Mixed));
        Assert.Equal("claude", r.Agents);
        Assert.Equal("claude-opus-4-7", r.Models);
    }

    // git-ai 1.3.4 wire format: agent_id is nested, overriden_lines is misspelled.
    // The implementation must read both shapes so real notes work end-to-end.
    private const string GitAi134Format = """
test1.json
  a32eb39a046df3b9 1-3
test2.json
  a32eb39a046df3b9 1-3
---
{
  "schema_version": "authorship/3.0.0",
  "git_ai_version": "1.3.4",
  "prompts": {
    "a32eb39a046df3b9": {
      "agent_id": { "tool": "claude", "id": "x", "model": "claude-opus-4-7" },
      "human_author": "Tester",
      "total_additions": 6,
      "accepted_lines": 6,
      "overriden_lines": 2
    }
  }
}
""";

    [Fact]
    public void Reads_git_ai_1_3_4_nested_agent_id_and_misspelled_overriden_lines()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(GitAi134Format));
        Assert.Equal(6, r.AgentLines);
        Assert.Equal(0, r.HumanLines);
        Assert.Equal(100m, r.AgentPercentage);
        Assert.Equal(2, r.OverriddenLines);            // came from "overriden_lines" (typo)
        Assert.Equal("claude", r.Agents);              // came from agent_id.tool
        Assert.Equal("claude-opus-4-7", r.Models);     // came from agent_id.model
    }
}
