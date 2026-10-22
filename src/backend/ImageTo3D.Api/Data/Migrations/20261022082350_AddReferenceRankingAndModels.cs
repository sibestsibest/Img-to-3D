using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceRankingAndModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputViewpoint",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "InputViewpointConfidence",
                table: "ReferenceSessions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastModelSearchJson",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]"); // existing sessions: no model search yet (an empty string is not JSON)

            migrationBuilder.AddColumn<DateTime>(
                name: "RankedAt",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankingMode",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankingNote",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupIndex",
                table: "ReferenceCandidates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "InputSimilarity",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "KindConfidence",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QuerySimilarity",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RankConfidence",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RankPosition",
                table: "ReferenceCandidates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankReasonsJson",
                table: "ReferenceCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RankScore",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RelationConfidence",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedKind",
                table: "ReferenceCandidates",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedRelation",
                table: "ReferenceCandidates",
                type: "TEXT",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedViewpoint",
                table: "ReferenceCandidates",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ViewpointConfidence",
                table: "ReferenceCandidates",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImageEmbeddingCache",
                columns: table => new
                {
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ImageSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageEmbeddingCache", x => new { x.Model, x.ImageSha256 });
                });

            migrationBuilder.CreateTable(
                name: "ModelCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LicenseRaw = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LicenseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Query = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDownloadable = table.Column<bool>(type: "INTEGER", nullable: true),
                    FaceCount = table.Column<int>(type: "INTEGER", nullable: true),
                    VertexCount = table.Column<int>(type: "INTEGER", nullable: true),
                    FileBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    HasThumbnail = table.Column<bool>(type: "INTEGER", nullable: false),
                    DownloadStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DownloadError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    EvaluationStatus = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeometryJson = table.Column<string>(type: "TEXT", nullable: true),
                    VisualSimilarity = table.Column<double>(type: "REAL", nullable: true),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelCandidates_ReferenceSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ReferenceSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCandidates_SessionId_Source_SourceItemId",
                table: "ModelCandidates",
                columns: new[] { "SessionId", "Source", "SourceItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageEmbeddingCache");

            migrationBuilder.DropTable(
                name: "ModelCandidates");

            migrationBuilder.DropColumn(
                name: "InputViewpoint",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "InputViewpointConfidence",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "LastModelSearchJson",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "RankedAt",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "RankingMode",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "RankingNote",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "GroupIndex",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "InputSimilarity",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "KindConfidence",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "QuerySimilarity",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "RankConfidence",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "RankPosition",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "RankReasonsJson",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "RankScore",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "RelationConfidence",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "SuggestedKind",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "SuggestedRelation",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "SuggestedViewpoint",
                table: "ReferenceCandidates");

            migrationBuilder.DropColumn(
                name: "ViewpointConfidence",
                table: "ReferenceCandidates");
        }
    }
}
