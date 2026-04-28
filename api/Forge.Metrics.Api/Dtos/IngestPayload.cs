namespace Forge.Metrics.Api.Dtos;

public sealed record IngestPayload(
    string RepoName,
    string CommitSha,
    string NoteContent,
    string? RepoUrl = null,
    string? Branch = null,
    bool IsDefaultBranch = false,
    string? CommitAuthor = null,
    string? Agent = null,
    string? Model = null,
    int? DiffAdditions = null,
    int? DiffDeletions = null,
    DateTime? CommittedAt = null);
