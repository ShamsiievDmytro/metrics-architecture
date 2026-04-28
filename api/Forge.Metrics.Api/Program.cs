using System.Text.Json;
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Configuration;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForgeOptions>(builder.Configuration.GetSection(ForgeOptions.SectionName));

builder.Services.AddDbContext<ForgeDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Forge AI Metrics", Version = "v1" });
    c.AddSecurityDefinition("ApiKey", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-API-Key",
        Description = "Team API key (from /api/ingest auth, set in your stack's .env)"
    });
    c.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" } }] = []
    });
});

builder.Services.AddScoped<ApiKeyFilter>();

var app = builder.Build();

await BootstrapDatabaseAsync(app.Services);

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forge AI Metrics v1"));

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("health");

app.MapIngest();
app.MapMetrics();
app.MapSetup();

app.Run();

static async Task BootstrapDatabaseAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForgeOptions>>().Value;
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");

    Exception? last = null;
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            last = null;
            break;
        }
        catch (Exception ex)
        {
            last = ex;
            logger.LogWarning("waiting for sql server (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
    if (last is not null) throw new InvalidOperationException("sql server unreachable after 30 attempts", last);

    var hash = ApiKeyHasher.Hash(opts.SeedTeamApiKey);
    if (!await db.Teams.AnyAsync(t => t.ApiKeyHash == hash))
    {
        db.Teams.Add(new Team { Name = opts.SeedTeamName, ApiKeyHash = hash });
        await db.SaveChangesAsync();
        logger.LogInformation("seeded team {Name}", opts.SeedTeamName);
    }
}

public partial class Program;  // for WebApplicationFactory if added later
