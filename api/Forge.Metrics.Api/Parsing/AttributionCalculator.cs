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

        var overridden = 0;
        foreach (var prompt in parsed.Prompts.Values)
            if (prompt["overridden_lines"] is { } v && v.GetValue<int>() is var n)
                overridden += n;

        var contributingPromptIds = parsed.Entries
            .Where(e => !e.IsHuman)
            .Select(e => e.AttributionId)
            .Distinct()
            .ToArray();

        string? JoinDistinct(string field) =>
            contributingPromptIds
                .Select(pid => parsed.Prompts.TryGetValue(pid, out var p) ? p[field]?.GetValue<string>() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .DefaultIfEmpty(null)
                .Aggregate((a, b) => a is null ? b : (b is null ? a : $"{a},{b}"));

        return new AttributionResult(
            agentLines,
            humanLines,
            pct,
            overridden,
            JoinDistinct("agent"),
            JoinDistinct("model"));
    }
}
