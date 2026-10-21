using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReferenceSearchCache",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HitsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceSearchCache", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HasInputImage = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    InputHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    InputDHash = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSearchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSearchJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LicenseRaw = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LicenseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Query = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DownloadError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    HasThumbnail = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThumbnailWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ThumbnailHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DHash = table.Column<long>(type: "INTEGER", nullable: true),
                    DuplicateOfId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DuplicateKind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    MatchesInput = table.Column<bool>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ImageKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Relation = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    HasFullImage = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceCandidates_ReferenceSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ReferenceSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceCandidates_SessionId_SortOrder",
                table: "ReferenceCandidates",
                columns: new[] { "SessionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceCandidates_SessionId_Source_SourceItemId",
                table: "ReferenceCandidates",
                columns: new[] { "SessionId", "Source", "SourceItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSearchCache_ExpiresAt",
                table: "ReferenceSearchCache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSessions_OwnerKeyHash_UpdatedAt",
                table: "ReferenceSessions",
                columns: new[] { "OwnerKeyHash", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceCandidates");

            migrationBuilder.DropTable(
                name: "ReferenceSearchCache");

            migrationBuilder.DropTable(
                name: "ReferenceSessions");
        }
    }
}
