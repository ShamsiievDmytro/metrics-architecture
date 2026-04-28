using Forge.Metrics.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Auth;

public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string TeamItemKey = "Forge.Team";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!http.Request.Headers.TryGetValue("X-API-Key", out var values) || values.Count == 0)
            return Results.Unauthorized();

        var apiKey = values.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Unauthorized();

        var db = http.RequestServices.GetRequiredService<ForgeDbContext>();
        var hash = ApiKeyHasher.Hash(apiKey);
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.ApiKeyHash == hash);
        if (team is null)
            return Results.Unauthorized();

        http.Items[TeamItemKey] = team;
        return await next(ctx);
    }
}

public static class HttpContextTeamExtensions
{
    public static Team RequireTeam(this HttpContext http) =>
        (Team)(http.Items[ApiKeyFilter.TeamItemKey]
               ?? throw new InvalidOperationException("Team missing from HttpContext.Items — did the ApiKeyFilter run?"));
}
