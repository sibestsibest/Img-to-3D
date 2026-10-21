using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartDesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartDesignProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MmPerModelUnit = table.Column<double>(type: "REAL", nullable: false),
                    FitAllowanceMinMm = table.Column<double>(type: "REAL", nullable: false),
                    FitAllowanceMaxMm = table.Column<double>(type: "REAL", nullable: false),
                    IssuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartDesignProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DesignParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentPartId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceNodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GeometryFile = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GeometryReportJson = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedCategory = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SuggestionConfidence = table.Column<double>(type: "REAL", nullable: false),
                    SuggestionReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EstimatedLengthMm = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedWidthMm = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedThicknessMm = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedOuterDiameterMm = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedInnerDiameterMm = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReviewStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Material = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ManufacturingMethod = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    ThicknessMm = table.Column<double>(type: "REAL", nullable: true),
                    DiameterMm = table.Column<double>(type: "REAL", nullable: true),
                    OuterDiameterMm = table.Column<double>(type: "REAL", nullable: true),
                    InnerDiameterMm = table.Column<double>(type: "REAL", nullable: true),
                    CutLengthMm = table.Column<double>(type: "REAL", nullable: true),
                    CutAngleDeg = table.Column<double>(type: "REAL", nullable: true),
                    FitAllowanceMm = table.Column<double>(type: "REAL", nullable: true),
                    ConfirmedLengthMm = table.Column<double>(type: "REAL", nullable: true),
                    ConfirmedWidthMm = table.Column<double>(type: "REAL", nullable: true),
                    ConfirmedHeightMm = table.Column<double>(type: "REAL", nullable: true),
                    StockDescription = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StockSource = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    NeedsFurtherModelling = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DesignParts_PartDesignProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PartDesignProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ChangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsUndone = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartOperations_PartDesignProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "PartDesignProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DesignParts_ProjectId_SortOrder",
                table: "DesignParts",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PartDesignProjects_OwnerKeyHash_UpdatedAt",
                table: "PartDesignProjects",
                columns: new[] { "OwnerKeyHash", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PartOperations_ProjectId_Sequence",
                table: "PartOperations",
                columns: new[] { "ProjectId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DesignParts");

            migrationBuilder.DropTable(
                name: "PartOperations");

            migrationBuilder.DropTable(
                name: "PartDesignProjects");
        }
    }
}
