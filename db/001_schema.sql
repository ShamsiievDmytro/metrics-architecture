-- Forge AI Metrics schema (spec §3). Idempotent — safe to re-run.
-- Reference only; runtime schema creation is handled by EF Core EnsureCreatedAsync().

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Teams')
BEGIN
  CREATE TABLE Teams (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(255) NOT NULL,
    ApiKeyHash NVARCHAR(255) NOT NULL UNIQUE,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
  );
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Commits')
BEGIN
  CREATE TABLE Commits (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    TeamId UNIQUEIDENTIFIER NOT NULL REFERENCES Teams(Id),
    RepoName NVARCHAR(500) NOT NULL,
    RepoUrl NVARCHAR(1000),
    CommitSha NVARCHAR(40) NOT NULL,
    Branch NVARCHAR(500),
    IsDefaultBranch BIT DEFAULT 0,
    CommitAuthor NVARCHAR(255),
    Agent NVARCHAR(100),
    Model NVARCHAR(255),
    AgentLines INT NOT NULL DEFAULT 0,
    HumanLines INT NOT NULL DEFAULT 0,
    OverriddenLines INT NOT NULL DEFAULT 0,
    AgentPercentage DECIMAL(5,1) NOT NULL DEFAULT 0,
    DiffAdditions INT NOT NULL DEFAULT 0,
    DiffDeletions INT NOT NULL DEFAULT 0,
    CommittedAt DATETIME2,
    IngestedAt DATETIME2 DEFAULT GETUTCDATE(),
    RawNote NVARCHAR(MAX),
    CONSTRAINT UQ_Commit UNIQUE(TeamId, RepoName, CommitSha)
  );

  CREATE INDEX IX_Commits_Team ON Commits(TeamId);
  CREATE INDEX IX_Commits_Repo ON Commits(TeamId, RepoName);
  CREATE INDEX IX_Commits_Author ON Commits(TeamId, CommitAuthor);
  CREATE INDEX IX_Commits_Date ON Commits(TeamId, CommittedAt);
  CREATE INDEX IX_Commits_Agent ON Commits(TeamId, Agent);
END;
