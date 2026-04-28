using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Dtos;
using Forge.Metrics.Api.Parsing;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Endpoints;

public static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest", async (IngestPayload payload, HttpContext http, ForgeDbContext db) =>
            {
                var team = http.RequireTeam();

                ParsedNote parsed;
                try { parsed = NoteParser.Parse(payload.NoteContent); }
                catch (FormatException ex) { return Results.BadRequest(new { error = ex.Message }); }

                var attribution = AttributionCalculator.Compute(parsed, payload.DiffAdditions);

                var existing = await db.Commits.FirstOrDefaultAsync(c =>
                    c.TeamId == team.Id &&
                    c.RepoName == payload.RepoName &&
                    c.CommitSha == payload.CommitSha);

                var duplicate = existing is not null;
                var row = existing ?? new Commit
                {
                    TeamId = team.Id,
                    RepoName = payload.RepoName,
                    CommitSha = payload.CommitSha,
                };

                row.RepoUrl = payload.RepoUrl;
                row.Branch = payload.Branch;
                row.IsDefaultBranch = payload.IsDefaultBranch;
                row.CommitAuthor = payload.CommitAuthor;
                row.Agent = attribution.Agents ?? payload.Agent;
                row.Model = attribution.Models ?? payload.Model;
                row.AgentLines = attribution.AgentLines;
                row.HumanLines = attribution.HumanLines;
                row.OverriddenLines = attribution.OverriddenLines;
                row.AgentPercentage = attribution.AgentPercentage;
                row.DiffAdditions = payload.DiffAdditions ?? 0;
                row.DiffDeletions = payload.DiffDeletions ?? 0;
                row.CommittedAt = payload.CommittedAt;
                row.RawNote = payload.NoteContent;

                if (!duplicate) db.Commits.Add(row);
                await db.SaveChangesAsync();

                return Results.Ok(new IngestResponse(
                    row.Id, row.AgentLines, row.HumanLines, row.AgentPercentage, row.OverriddenLines, duplicate));
            })
            .WithTags("ingest")
            .WithSummary("Ingest a git-ai note from a developer machine")
            .AddEndpointFilter<ApiKeyFilter>();

        return app;
    }
}
