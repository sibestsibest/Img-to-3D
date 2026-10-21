using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    StageProgress = table.Column<double>(type: "REAL", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OriginalFormat = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    OriginalSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    InferenceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VertexCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TriangleCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelSizeX = table.Column<double>(type: "REAL", nullable: true),
                    ModelSizeY = table.Column<double>(type: "REAL", nullable: true),
                    ModelSizeZ = table.Column<double>(type: "REAL", nullable: true),
                    MmPerModelUnit = table.Column<double>(type: "REAL", nullable: true),
                    ScaleAxis = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ScaleTargetMm = table.Column<double>(type: "REAL", nullable: true),
                    HasGlb = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasStl = table.Column<bool>(type: "INTEGER", nullable: false),
                    StlError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_OwnerKeyHash_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "OwnerKeyHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_OwnerKeyHash_IdempotencyKey",
                table: "GenerationJobs",
                columns: new[] { "OwnerKeyHash", "IdempotencyKey" },
                unique: true,
                filter: "IdempotencyKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_Status",
                table: "GenerationJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationJobs");
        }
    }
}
