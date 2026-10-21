using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace ImageTo3D.Api.Data;

/// <summary>
/// Applies EF Core migrations. Databases created before migrations existed (by EnsureCreated) have
/// the GenerationJobs table but no migration history; for those the baseline migration is recorded
/// as applied, after checking that the existing table has every column the baseline expects, so no
/// data is touched and later migrations apply normally.
/// </summary>
public static class DatabaseInitializer
{
    public const string BaselineMigration = "20261020094019_InitialCreate";

    private static readonly string[] BaselineJobColumns =
    [
        "Id", "OwnerKeyHash", "IdempotencyKey", "Status", "Stage", "StageProgress", "ErrorCode", "ErrorMessage",
        "CancelRequested", "OriginalFileName", "OriginalFormat", "OriginalSizeBytes", "ImageWidth", "ImageHeight",
        "InferenceJobId", "CreatedAt", "StartedAt", "CompletedAt", "UpdatedAt", "VertexCount", "TriangleCount",
        "ModelSizeX", "ModelSizeY", "ModelSizeZ", "MmPerModelUnit", "ScaleAxis", "ScaleTargetMm", "HasGlb", "HasStl", "StlError",
    ];

    public static void Initialize(AppDbContext db, ILogger logger)
    {
        // Let EF create a missing database file first: its SQLite creator switches the file to WAL
        // journaling (as EnsureCreated did). Probing the history table first would create an empty
        // file in rollback-journal mode instead, and concurrent writers then fail with SQLITE_BUSY.
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!creator.Exists())
            creator.Create();

        var history = db.GetService<IHistoryRepository>();
        if (!history.Exists() && TableExists(db, "GenerationJobs"))
        {
            var missing = BaselineJobColumns.Except(Columns(db, "GenerationJobs"), StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "The existing database was created by an older version and lacks columns " + string.Join(", ", missing) +
                    ". Back it up and delete it, or add the columns manually; it was not modified.");
            }

            logger.LogInformation("Existing database without migration history: recording baseline migration {Migration}", BaselineMigration);
            db.Database.ExecuteSqlRaw(history.GetCreateIfNotExistsScript());
            db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(BaselineMigration, ProductInfo.GetVersion())));
        }

        db.Database.Migrate();
    }

    private static bool TableExists(AppDbContext db, string table) =>
        db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {table}").AsEnumerable().First() > 0;

    private static List<string> Columns(AppDbContext db, string table) =>
        db.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM pragma_table_info({table})").ToList();
}
