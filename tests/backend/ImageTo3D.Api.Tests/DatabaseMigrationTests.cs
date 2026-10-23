using ImageTo3D.Api.Data;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ImageTo3D.Api.Tests;

public class DatabaseMigrationTests
{
    /// <summary>Schema exactly as EnsureCreated produced it before migrations existed (dumped from a real database).</summary>
    private const string LegacySchema = """
        CREATE TABLE "GenerationJobs" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_GenerationJobs" PRIMARY KEY,
            "OwnerKeyHash" TEXT NOT NULL, "IdempotencyKey" TEXT NULL, "Status" TEXT NOT NULL, "Stage" TEXT NULL,
            "StageProgress" REAL NULL, "ErrorCode" TEXT NULL, "ErrorMessage" TEXT NULL, "CancelRequested" INTEGER NOT NULL,
            "OriginalFileName" TEXT NOT NULL, "OriginalFormat" TEXT NOT NULL, "OriginalSizeBytes" INTEGER NOT NULL,
            "ImageWidth" INTEGER NOT NULL, "ImageHeight" INTEGER NOT NULL, "InferenceJobId" TEXT NULL, "CreatedAt" TEXT NOT NULL,
            "StartedAt" TEXT NULL, "CompletedAt" TEXT NULL, "UpdatedAt" TEXT NOT NULL, "VertexCount" INTEGER NULL,
            "TriangleCount" INTEGER NULL, "ModelSizeX" REAL NULL, "ModelSizeY" REAL NULL, "ModelSizeZ" REAL NULL,
            "MmPerModelUnit" REAL NULL, "ScaleAxis" TEXT NULL, "ScaleTargetMm" REAL NULL, "HasGlb" INTEGER NOT NULL,
            "HasStl" INTEGER NOT NULL, "StlError" TEXT NULL);
        CREATE INDEX "IX_GenerationJobs_OwnerKeyHash_CreatedAt" ON "GenerationJobs" ("OwnerKeyHash", "CreatedAt");
        CREATE UNIQUE INDEX "IX_GenerationJobs_OwnerKeyHash_IdempotencyKey" ON "GenerationJobs" ("OwnerKeyHash", "IdempotencyKey") WHERE IdempotencyKey IS NOT NULL;
        CREATE INDEX "IX_GenerationJobs_Status" ON "GenerationJobs" ("Status");
        """;

    private const string LegacyRow = """
        INSERT INTO "GenerationJobs" VALUES ('8b1c3f0e-0000-4000-8000-000000000001', 'owner', NULL, 'Succeeded', NULL, NULL, NULL, NULL, 0,
            'part.png', 'png', 100, 64, 64, NULL, '2026-10-01 10:00:00', NULL, NULL, '2026-10-01 10:00:00', 8, 12,
            0.5, 1.0, 0.25, 100, 'Longest', 100, 1, 1, NULL);
        """;

    private static string CreateLegacyDatabase(string directory, string schema)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema + LegacyRow;
        command.ExecuteNonQuery();
        return path;
    }

    private static List<string> Query(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    [Fact]
    public async Task LegacyDatabase_IsBaselinedAndUpgradedWithoutLosingJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        var path = CreateLegacyDatabase(directory, LegacySchema);
        using (var factory = new ApiFactory(new() { ["Storage:DatabasePath"] = path }))
        {
            factory.CreateClient(); // starts the host, which runs the initializer

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.Jobs.SingleAsync();
            Assert.Equal("part.png", job.OriginalFileName);
            Assert.Equal(100, job.MmPerModelUnit);
            Assert.Empty(await db.PartDesigns.ToListAsync()); // new tables exist and are queryable
            Assert.Empty(await db.ReferenceSessions.ToListAsync());
        }

        SqliteConnection.ClearAllPools();
        var applied = Query(path, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId");
        Assert.Equal(DatabaseInitializer.BaselineMigration, applied[0]);
        Assert.Contains(applied, m => m.EndsWith("_AddPartDesign", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("_AddReferenceSearch", StringComparison.Ordinal));
        Assert.Equal(["1"], Query(path, "SELECT CAST(COUNT(*) AS TEXT) FROM GenerationJobs"));
    }

    [Fact]
    public void LegacyDatabaseMissingColumns_IsRefusedAndLeftUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        var path = CreateLegacyDatabase(directory, LegacySchema.Replace("\"StlError\" TEXT NULL", "\"Extra\" TEXT NULL"));

        using var factory = new ApiFactory(new() { ["Storage:DatabasePath"] = path });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("StlError", error.ToString());
        SqliteConnection.ClearAllPools();
        Assert.Empty(Query(path, "SELECT name FROM sqlite_master WHERE name = '__EFMigrationsHistory'"));
        Assert.Empty(Query(path, "SELECT name FROM sqlite_master WHERE name = 'PartDesignProjects'"));
    }

    [Fact]
    public void FreshDatabase_AppliesAllMigrations()
    {
        using var factory = new ApiFactory();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Equal(8, db.Database.GetAppliedMigrations().Count());
        // WAL lets the background worker and requests write concurrently (EnsureCreated used to set it).
        Assert.Equal("wal", db.Database.SqlQueryRaw<string>("SELECT journal_mode AS \"Value\" FROM pragma_journal_mode").AsEnumerable().Single());
    }

    [Fact]
    public void ReconstructionMigration_KeepsExistingProjectsAsMadeFromJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phase5.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        using (var db = new AppDbContext(options))
        {
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db)
                .Migrate("20261022082350_AddReferenceRankingAndModels");
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "PartDesignProjects" ("Id", "OwnerKeyHash", "Name", "SourceJobId", "SourceFileName", "MmPerModelUnit",
                    "FitAllowanceMinMm", "FitAllowanceMaxMm", "IssuesJson", "CreatedAt", "UpdatedAt")
                VALUES ('7f000000-0000-4000-8000-000000000001', 'owner', 'Old project', '8b1c3f0e-0000-4000-8000-000000000001', 'part.png', 100,
                    -0.5, 0.5, '[]', '2026-10-01 10:00:00', '2026-10-01 10:00:00');
                """);
            db.Database.Migrate();

            var project = db.PartDesigns.AsNoTracking().Single();
            Assert.Equal("generation_job", project.SourceKind);
            Assert.Equal(Guid.Parse("8b1c3f0e-0000-4000-8000-000000000001"), project.SourceJobId);
            Assert.Equal(100, project.MmPerModelUnit);
            Assert.Null(project.SourceModelCandidateId);
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ParametricMigration_IsAdditiveAndKeepsExistingRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phase7.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        using (var db = new AppDbContext(options))
        {
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db)
                .Migrate("20261023034126_AddPartRelations");
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "PartDesignProjects" ("Id", "OwnerKeyHash", "Name", "SourceKind", "SourceJobId", "SourceFileName", "MmPerModelUnit",
                    "FitAllowanceMinMm", "FitAllowanceMaxMm", "IssuesJson", "CreatedAt", "UpdatedAt")
                VALUES ('7f000000-0000-4000-8000-000000000002', 'owner', 'Before parametric', 'generation_job', '8b1c3f0e-0000-4000-8000-000000000002', 'part.png', 100,
                    -0.5, 0.5, '[]', '2026-10-01 10:00:00', '2026-10-01 10:00:00');
                """);
            db.Database.Migrate();

            var project = db.PartDesigns.AsNoTracking().Single();
            Assert.Equal(("Before parametric", "generation_job", 100.0), (project.Name, project.SourceKind, project.MmPerModelUnit));
            Assert.Null(project.ParametricTemplate);
            Assert.Null(project.ParametricParametersJson);
            Assert.Empty(db.Database.GetPendingMigrations());
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
