namespace Forge.Metrics.Api.Data;

public class Commit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public string RepoName { get; set; } = "";
    public string? RepoUrl { get; set; }
    public string CommitSha { get; set; } = "";
    public string? Branch { get; set; }
    public bool IsDefaultBranch { get; set; }
    public string? CommitAuthor { get; set; }
    public string? Agent { get; set; }
    public string? Model { get; set; }
    public int AgentLines { get; set; }
    public int HumanLines { get; set; }
    public int OverriddenLines { get; set; }
    public decimal AgentPercentage { get; set; }
    public int DiffAdditions { get; set; }
    public int DiffDeletions { get; set; }
    public DateTime? CommittedAt { get; set; }
    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;
    public string? RawNote { get; set; }
}
