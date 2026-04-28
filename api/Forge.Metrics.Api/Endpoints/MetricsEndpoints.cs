using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics")
            .WithTags("metrics")
            .AddEndpointFilter<ApiKeyFilter>();

        group.MapGet("/summary", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, days) = ParsePeriod(period);
            var team = http.RequireTeam();
            var q = db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff);

            var totalCommits = await q.CountAsync();
            var aiCommits = await q.CountAsync(c => c.AgentLines > 0);
            var aiLines = await q.SumAsync(c => (int?)c.AgentLines) ?? 0;
            var humanLines = await q.SumAsync(c => (int?)c.HumanLines) ?? 0;

            return Results.Ok(new SummaryResponse(
                days,
                totalCommits,
                aiCommits,
                totalCommits == 0 ? 0m : Math.Round((decimal)aiCommits / totalCommits * 100m, 1),
                aiLines,
                humanLines));
        });

        group.MapGet("/by-agent", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var rows = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.Agent)
                .Select(g => new ByAgentRow(
                    g.Key,
                    g.Count(),
                    g.Sum(c => c.AgentLines),
                    Math.Round(g.Average(c => c.AgentPercentage), 1)))
                .ToListAsync();

            return Results.Ok(rows);
        });

        group.MapGet("/by-developer", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var raw = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.CommitAuthor)
                .Select(g => new
                {
                    Author = g.Key,
                    Commits = g.Count(),
                    AiLines = g.Sum(c => c.AgentLines),
                    HumanLines = g.Sum(c => c.HumanLines),
                })
                .ToListAsync();

            var rows = raw.Select(r =>
            {
                var total = r.AiLines + r.HumanLines;
                var pct = total == 0 ? 0m : Math.Round((decimal)r.AiLines / total * 100m, 1);
                return new ByDeveloperRow(r.Author, r.Commits, r.AiLines, pct);
            });

            return Results.Ok(rows);
        });

        group.MapGet("/by-repo", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var raw = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.RepoName)
                .Select(g => new
                {
                    RepoName = g.Key,
                    Commits = g.Count(),
                    AiLines = g.Sum(c => c.AgentLines),
                    HumanLines = g.Sum(c => c.HumanLines),
                })
                .ToListAsync();

            var rows = raw.Select(r =>
            {
                var total = r.AiLines + r.HumanLines;
                var pct = total == 0 ? 0m : Math.Round((decimal)r.AiLines / total * 100m, 1);
                return new ByRepoRow(r.RepoName, r.Commits, r.AiLines, pct);
            });

            return Results.Ok(rows);
        });

        return app;
    }

    private static (DateTime cutoff, int days) ParsePeriod(string? period)
    {
        var raw = string.IsNullOrEmpty(period) ? "30d" : period;
        if (!raw.EndsWith('d') || !int.TryParse(raw.AsSpan(0, raw.Length - 1), out var days) || days <= 0)
            throw new BadHttpRequestException($"invalid period '{raw}', expected e.g. '7d', '30d', '90d'");
        return (DateTime.UtcNow.AddDays(-days), days);
    }
}
