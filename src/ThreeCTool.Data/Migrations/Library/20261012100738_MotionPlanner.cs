using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Library
{
    /// <inheritdoc />
    public partial class MotionPlanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "JunctionDeviation",
                table: "MachineProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 0.01);

            migrationBuilder.AddColumn<int>(
                name: "PlannerBlocks",
                table: "MachineProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JunctionDeviation",
                table: "MachineProfiles");

            migrationBuilder.DropColumn(
                name: "PlannerBlocks",
                table: "MachineProfiles");
        }
    }
}
