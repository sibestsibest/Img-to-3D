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
    }
}
