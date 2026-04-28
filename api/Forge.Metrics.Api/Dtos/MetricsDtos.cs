namespace Forge.Metrics.Api.Dtos;

public sealed record SummaryResponse(
    int PeriodDays,
    int TotalCommits,
    int AiCommits,
    decimal AiPercentage,
    int TotalAiLines,
    int TotalHumanLines);

public sealed record ByAgentRow(string? Agent, int Commits, int AiLines, decimal AvgPercentage);
public sealed record ByDeveloperRow(string? Author, int Commits, int AiLines, decimal AiPercentage);
public sealed record ByRepoRow(string RepoName, int Commits, int AiLines, decimal AiPercentage);
