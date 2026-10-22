using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReconstructionDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReconstructionDesignId",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconstructionExtraIdsJson",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReconstructionJobId",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconstructionMessage",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReconstructionModelId",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconstructionRoute",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconstructionStartedAt",
                table: "ReferenceSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconstructionState",
                table: "ReferenceSessions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceJobId",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "SourceAttribution",
                table: "PartDesignProjects",
                type: "TEXT",
                maxLength: 700,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "PartDesignProjects",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "generation_job"); // every project before this migration was made from a generation job

            migrationBuilder.AddColumn<string>(
                name: "SourceLicense",
                table: "PartDesignProjects",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceModelCandidateId",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceReferenceSessionId",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReconstructionDesignId",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionExtraIdsJson",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionJobId",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionMessage",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionModelId",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionRoute",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionStartedAt",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "ReconstructionState",
                table: "ReferenceSessions");

            migrationBuilder.DropColumn(
                name: "SourceAttribution",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "SourceLicense",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "SourceModelCandidateId",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "SourceReferenceSessionId",
                table: "PartDesignProjects");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceJobId",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
