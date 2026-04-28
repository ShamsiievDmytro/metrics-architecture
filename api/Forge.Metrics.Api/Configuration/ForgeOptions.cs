namespace Forge.Metrics.Api.Configuration;

public sealed class ForgeOptions
{
    public const string SectionName = "Forge";

    public string PublicApiUrl { get; init; } = "http://localhost:8000";
    public string SeedTeamName { get; init; } = "Platform Team";
    public string SeedTeamApiKey { get; init; } = "";
    public string ScriptsPath { get; init; } = "/scripts";
}
