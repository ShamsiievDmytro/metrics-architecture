using Forge.Metrics.Api.Configuration;
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forge.Metrics.Api.Endpoints;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/setup/{teamId:guid}/{apiKey}", async (
            Guid teamId, string apiKey,
            ForgeDbContext db,
            IOptions<ForgeOptions> opts) =>
        {
            var team = await ValidateAsync(db, teamId, apiKey);
            if (team is null) return Results.NotFound("team or api key not found");

            var tmplPath = Path.Combine(opts.Value.ScriptsPath, "setup.sh.tmpl");
            var rendered = (await File.ReadAllTextAsync(tmplPath))
                .Replace("__API_URL__", opts.Value.PublicApiUrl)
                .Replace("__API_KEY__", apiKey)
                .Replace("__TEAM_ID__", team.Id.ToString());

            return Results.Text(rendered, "text/x-shellscript");
        })
        .WithTags("setup")
        .WithSummary("Bash setup script for the developer machine");

        app.MapGet("/setup/{teamId:guid}/{apiKey}/enrich-and-post.sh", async (
            Guid teamId, string apiKey,
            ForgeDbContext db,
            IOptions<ForgeOptions> opts) =>
        {
            var team = await ValidateAsync(db, teamId, apiKey);
            if (team is null) return Results.NotFound();

            var path = Path.Combine(opts.Value.ScriptsPath, "enrich-and-post.sh");
            var body = await File.ReadAllTextAsync(path);
            return Results.Text(body, "text/x-shellscript");
        })
        .WithTags("setup")
        .WithSummary("Latest enrich-and-post.sh hook script");

        return app;
    }

    private static async Task<Team?> ValidateAsync(ForgeDbContext db, Guid teamId, string apiKey)
    {
        var hash = ApiKeyHasher.Hash(apiKey);
        var team = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApiKeyHash == hash);
        return team is null || team.Id != teamId ? null : team;
    }
}
