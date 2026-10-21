using ImageTo3D.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ImageTo3D.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GenerationJob> Jobs => Set<GenerationJob>();

    public DbSet<PartDesignProject> PartDesigns => Set<PartDesignProject>();

    public DbSet<DesignPart> DesignParts => Set<DesignPart>();

    public DbSet<PartOperation> PartOperations => Set<PartOperation>();

    public DbSet<DesignRelease> DesignReleases => Set<DesignRelease>();

    public DbSet<ReferenceSession> ReferenceSessions => Set<ReferenceSession>();

    public DbSet<ReferenceCandidate> ReferenceCandidates => Set<ReferenceCandidate>();

    public DbSet<ReferenceSearchCacheEntry> ReferenceSearchCache => Set<ReferenceSearchCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<GenerationJob>();
        job.ToTable("GenerationJobs");
        job.HasKey(j => j.Id);
        job.Property(j => j.OwnerKeyHash).HasMaxLength(64).IsRequired();
        job.Property(j => j.IdempotencyKey).HasMaxLength(64);
        job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        job.Property(j => j.ScaleAxis).HasConversion<string>().HasMaxLength(16);
        job.Property(j => j.Stage).HasMaxLength(32);
        job.Property(j => j.ErrorCode).HasMaxLength(32);
        job.Property(j => j.ErrorMessage).HasMaxLength(500);
        job.Property(j => j.StlError).HasMaxLength(500);
        job.Property(j => j.OriginalFileName).HasMaxLength(128);
        job.Property(j => j.OriginalFormat).HasMaxLength(8);
        job.Ignore(j => j.IsTerminal);
        job.Ignore(j => j.SizeMm);

        job.HasIndex(j => new { j.OwnerKeyHash, j.CreatedAt });
        job.HasIndex(j => new { j.OwnerKeyHash, j.IdempotencyKey }).IsUnique().HasFilter("IdempotencyKey IS NOT NULL");
        job.HasIndex(j => j.Status);

        // No foreign key to GenerationJobs: a project keeps its own copy of the source and must
        // outlive job retention.
        var project = modelBuilder.Entity<PartDesignProject>();
        project.ToTable("PartDesignProjects");
        project.HasKey(p => p.Id);
        project.Property(p => p.OwnerKeyHash).HasMaxLength(64).IsRequired();
        project.Property(p => p.Name).HasMaxLength(100).IsRequired();
        project.Property(p => p.SourceFileName).HasMaxLength(128).IsRequired();
        project.HasIndex(p => new { p.OwnerKeyHash, p.UpdatedAt });
        project.HasMany(p => p.Parts).WithOne().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        project.HasMany(p => p.Operations).WithOne().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Cascade);

        var part = modelBuilder.Entity<DesignPart>();
        part.ToTable("DesignParts");
        part.HasKey(p => p.Id);
        part.Property(p => p.Name).HasMaxLength(100).IsRequired();
        part.Property(p => p.Origin).HasMaxLength(16);
        part.Property(p => p.SourceNodeName).HasMaxLength(100);
        part.Property(p => p.GeometryFile).HasMaxLength(64);
        part.Property(p => p.State).HasConversion<string>().HasMaxLength(16);
        part.Property(p => p.Category).HasMaxLength(16);
        part.Property(p => p.SuggestedCategory).HasMaxLength(16);
        part.Property(p => p.ReviewStatus).HasMaxLength(16);
        part.Property(p => p.Material).HasMaxLength(100);
        part.Property(p => p.ManufacturingMethod).HasMaxLength(100);
        part.Property(p => p.StockDescription).HasMaxLength(200);
        part.Property(p => p.StockSource).HasMaxLength(200);
        part.Property(p => p.Notes).HasMaxLength(2000);
        part.HasIndex(p => new { p.ProjectId, p.SortOrder });

        var operation = modelBuilder.Entity<PartOperation>();
        operation.ToTable("PartOperations");
        operation.HasKey(o => o.Id);
        operation.Property(o => o.Kind).HasMaxLength(16);
        operation.Property(o => o.Summary).HasMaxLength(200);
        operation.HasIndex(o => new { o.ProjectId, o.Sequence }).IsUnique();

        // Not loaded with the project (releases are listed separately); the database cascades the delete.
        var release = modelBuilder.Entity<DesignRelease>();
        release.ToTable("DesignReleases");
        release.HasKey(r => r.Id);
        release.Property(r => r.Note).HasMaxLength(500);
        release.Property(r => r.PackageFile).HasMaxLength(64);
        release.Property(r => r.Sha256).HasMaxLength(64);
        release.HasOne<PartDesignProject>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        release.HasIndex(r => new { r.ProjectId, r.Revision }).IsUnique();

        // Like part designs, no foreign key to GenerationJobs: a session keeps its own copy of the photo.
        var session = modelBuilder.Entity<ReferenceSession>();
        session.ToTable("ReferenceSessions");
        session.HasKey(s => s.Id);
        session.Property(s => s.OwnerKeyHash).HasMaxLength(64).IsRequired();
        session.Property(s => s.Name).HasMaxLength(100).IsRequired();
        session.Property(s => s.Query).HasMaxLength(200).IsRequired();
        session.HasIndex(s => new { s.OwnerKeyHash, s.UpdatedAt });
        session.HasMany(s => s.Candidates).WithOne().HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);

        var candidate = modelBuilder.Entity<ReferenceCandidate>();
        candidate.ToTable("ReferenceCandidates");
        candidate.HasKey(c => c.Id);
        candidate.Property(c => c.Origin).HasMaxLength(16).IsRequired();
        candidate.Property(c => c.Source).HasMaxLength(32).IsRequired();
        candidate.Property(c => c.SourceItemId).HasMaxLength(100).IsRequired();
        candidate.Property(c => c.Title).HasMaxLength(300).IsRequired();
        candidate.Property(c => c.PageUrl).HasMaxLength(2000);
        candidate.Property(c => c.ImageUrl).HasMaxLength(2000);
        candidate.Property(c => c.ThumbnailUrl).HasMaxLength(2000);
        candidate.Property(c => c.Author).HasMaxLength(200);
        candidate.Property(c => c.License).HasMaxLength(32);
        candidate.Property(c => c.LicenseRaw).HasMaxLength(100);
        candidate.Property(c => c.LicenseUrl).HasMaxLength(2000);
        candidate.Property(c => c.Query).HasMaxLength(200);
        candidate.Property(c => c.DownloadStatus).HasMaxLength(16);
        candidate.Property(c => c.DownloadError).HasMaxLength(300);
        candidate.Property(c => c.Sha256).HasMaxLength(64);
        candidate.Property(c => c.DuplicateKind).HasMaxLength(8);
        candidate.Property(c => c.Decision).HasMaxLength(16);
        candidate.Property(c => c.ImageKind).HasMaxLength(16);
        candidate.Property(c => c.Relation).HasMaxLength(24);
        candidate.Property(c => c.Note).HasMaxLength(1000);
        candidate.HasIndex(c => new { c.SessionId, c.Source, c.SourceItemId }).IsUnique();
        candidate.HasIndex(c => new { c.SessionId, c.SortOrder });

        var cache = modelBuilder.Entity<ReferenceSearchCacheEntry>();
        cache.ToTable("ReferenceSearchCache");
        cache.HasKey(e => e.Key);
        cache.Property(e => e.Key).HasMaxLength(64);
        cache.Property(e => e.Source).HasMaxLength(32).IsRequired();
        cache.Property(e => e.Query).HasMaxLength(200).IsRequired();
        cache.HasIndex(e => e.ExpiresAt);
    }
}
