using System.Text.Json.Nodes;

namespace Forge.Metrics.Api.Parsing;

public sealed record AttributionResult(
    int AgentLines,
    int HumanLines,
    decimal AgentPercentage,
    int OverriddenLines,
    string? Agents,
    string? Models);

public static class AttributionCalculator
{
    public static AttributionResult Compute(ParsedNote parsed, int? enrichedDiffAdditions = null)
    {
        var agentLines = parsed.Entries.Where(e => !e.IsHuman).Sum(e => e.LineCount);
        var humanLines = parsed.Entries.Where(e => e.IsHuman).Sum(e => e.LineCount);

        if (agentLines == 0 && humanLines == 0 && enrichedDiffAdditions is > 0)
            humanLines = enrichedDiffAdditions.Value;

        var total = agentLines + humanLines;
        var pct = total > 0
            ? Math.Round((decimal)agentLines / total * 100m, 1)
            : 0m;

        // git-ai 1.3.4 emits "overriden_lines" (typo); the spec uses "overridden_lines".
        // Read either so both real notes and spec-shaped notes work.
        var overridden = 0;
        foreach (var prompt in parsed.Prompts.Values)
        {
            var node = prompt["overriden_lines"] ?? prompt["overridden_lines"];
            if (node is not null) overridden += node.GetValue<int>();
        }

        var contributingPromptIds = parsed.Entries
            .Where(e => !e.IsHuman)
            .Select(e => e.AttributionId)
            .Distinct()
            .ToArray();

        string? AgentField(JsonObject p) =>
            (p["agent_id"] as JsonObject)?["tool"]?.GetValue<string>()
            ?? p["agent"]?.GetValue<string>();

        string? ModelField(JsonObject p) =>
            (p["agent_id"] as JsonObject)?["model"]?.GetValue<string>()
            ?? p["model"]?.GetValue<string>();

        string? Join(Func<JsonObject, string?> getField) =>
            contributingPromptIds
                .Select(pid => parsed.Prompts.TryGetValue(pid, out var p) ? getField(p) : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .DefaultIfEmpty(null)
                .Aggregate((a, b) => a is null ? b : (b is null ? a : $"{a},{b}"));

        return new AttributionResult(
            agentLines,
            humanLines,
            pct,
            overridden,
            Join(AgentField),
            Join(ModelField));
    }
}
