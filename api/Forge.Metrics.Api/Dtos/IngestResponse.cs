namespace Forge.Metrics.Api.Dtos;

public sealed record IngestResponse(
    Guid CommitId,
    int AgentLines,
    int HumanLines,
    decimal AgentPercentage,
    int OverriddenLines,
    bool Duplicate);
