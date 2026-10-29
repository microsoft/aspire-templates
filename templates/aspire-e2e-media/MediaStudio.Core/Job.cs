using Microsoft.EntityFrameworkCore;

namespace MediaStudio.Core;

public sealed class Job
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public string Hash { get; set; } = "";
    public string State { get; set; } = "Queued";
    public int Attempts { get; set; }
    public int Generation { get; set; }
    public bool SimulateFailure { get; set; }
    public string? Error { get; set; }
    public string? TraceParent { get; set; }
    public Guid? Lease { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset NextDispatch { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaDb(DbContextOptions<MediaDb> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Job>().HasIndex(job => job.RequestKey).IsUnique();
        model.Entity<Job>().Property(job => job.RequestKey).HasMaxLength(100);
        model.Entity<Job>().Property(job => job.Name).HasMaxLength(160);
        model.Entity<Job>().Property(job => job.Hash).HasMaxLength(64);
        model.Entity<Job>().HasIndex(job => new { job.State, job.NextDispatch });
    }
}

public sealed record WorkItem(Guid Id, int Generation, string? TraceParent);
