using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotogrammetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourcePhotogrammetryJobId",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PhotogrammetryJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Pipeline = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NodesDone = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    EngineVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SourceReferenceSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VertexCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TriangleCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelSizeX = table.Column<double>(type: "REAL", nullable: true),
                    ModelSizeY = table.Column<double>(type: "REAL", nullable: true),
                    ModelSizeZ = table.Column<double>(type: "REAL", nullable: true),
                    InspectionJson = table.Column<string>(type: "TEXT", nullable: true),
                    OutputsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    ScaleStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MmPerModelUnit = table.Column<double>(type: "REAL", nullable: true),
                    ScaleAxis = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ScaleTargetMm = table.Column<double>(type: "REAL", nullable: true),
                    HasStl = table.Column<bool>(type: "INTEGER", nullable: false),
                    StlError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotogrammetryJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PhotogrammetryPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DHash = table.Column<long>(type: "INTEGER", nullable: false),
                    Sharpness = table.Column<double>(type: "REAL", nullable: false),
                    HasExif = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotogrammetryPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotogrammetryPhotos_PhotogrammetryJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PhotogrammetryJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotogrammetryJobs_OwnerKeyHash_CreatedAt",
                table: "PhotogrammetryJobs",
                columns: new[] { "OwnerKeyHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotogrammetryJobs_Status",
                table: "PhotogrammetryJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PhotogrammetryPhotos_JobId_SortOrder",
                table: "PhotogrammetryPhotos",
                columns: new[] { "JobId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotogrammetryPhotos");

            migrationBuilder.DropTable(
                name: "PhotogrammetryJobs");

            migrationBuilder.DropColumn(
                name: "SourcePhotogrammetryJobId",
                table: "PartDesignProjects");
        }
    }
}
