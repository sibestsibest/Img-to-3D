using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ImageTo3D.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParametricDesigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParametricEngineVersion",
                table: "PartDesignProjects",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametricFilesJson",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametricParametersJson",
                table: "PartDesignProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametricTemplate",
                table: "PartDesignProjects",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "GenerationJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParametricEngineVersion",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "ParametricFilesJson",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "ParametricParametersJson",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "ParametricTemplate",
                table: "PartDesignProjects");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "GenerationJobs");
        }
    }
}
