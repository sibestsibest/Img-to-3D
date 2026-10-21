using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DesignReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PackageFile = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HistorySequence = table.Column<int>(type: "INTEGER", nullable: false),
                    PartCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DesignReleases_PartDesignProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PartDesignProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DesignReleases_ProjectId_Revision",
                table: "DesignReleases",
                columns: new[] { "ProjectId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DesignReleases");
        }
    }
}
