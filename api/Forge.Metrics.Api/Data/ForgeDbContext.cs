using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Data;

public class ForgeDbContext(DbContextOptions<ForgeDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Commit> Commits => Set<Commit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("Teams");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(255).IsRequired();
            e.Property(t => t.ApiKeyHash).HasMaxLength(255).IsRequired();
            e.HasIndex(t => t.ApiKeyHash).IsUnique();
        });

        modelBuilder.Entity<Commit>(e =>
        {
            e.ToTable("Commits");
            e.HasKey(c => c.Id);
            e.Property(c => c.RepoName).HasMaxLength(500).IsRequired();
            e.Property(c => c.RepoUrl).HasMaxLength(1000);
            e.Property(c => c.CommitSha).HasMaxLength(40).IsRequired();
            e.Property(c => c.Branch).HasMaxLength(500);
            e.Property(c => c.CommitAuthor).HasMaxLength(255);
            e.Property(c => c.Agent).HasMaxLength(100);
            e.Property(c => c.Model).HasMaxLength(255);
            e.Property(c => c.AgentPercentage).HasPrecision(5, 1);

            e.HasIndex(c => new { c.TeamId, c.RepoName, c.CommitSha })
              .IsUnique()
              .HasDatabaseName("UQ_Commit");
            e.HasIndex(c => c.TeamId).HasDatabaseName("IX_Commits_Team");
            e.HasIndex(c => new { c.TeamId, c.RepoName }).HasDatabaseName("IX_Commits_Repo");
            e.HasIndex(c => new { c.TeamId, c.CommitAuthor }).HasDatabaseName("IX_Commits_Author");
            e.HasIndex(c => new { c.TeamId, c.CommittedAt }).HasDatabaseName("IX_Commits_Date");
            e.HasIndex(c => new { c.TeamId, c.Agent }).HasDatabaseName("IX_Commits_Agent");
        });
    }
}
