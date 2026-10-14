using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Project
{
    /// <inheritdoc />
    public partial class MultiSideSetups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Anchor",
                table: "Setups",
                type: "TEXT",
                nullable: false,
                defaultValue: "Center");

            migrationBuilder.AddColumn<double>(
                name: "DepthLimit",
                table: "Setups",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Face",
                table: "Setups",
                type: "TEXT",
                nullable: false,
                defaultValue: "Top");

            migrationBuilder.AddColumn<double>(
                name: "FrameWidth",
                table: "Setups",
                type: "REAL",
                nullable: false,
                defaultValue: 10.0);

            migrationBuilder.AddColumn<int>(
                name: "Spin",
                table: "Setups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TabCount",
                table: "Setups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "TabThickness",
                table: "Setups",
                type: "REAL",
                nullable: false,
                defaultValue: 2.0);

            migrationBuilder.AddColumn<double>(
                name: "TabWidth",
                table: "Setups",
                type: "REAL",
                nullable: false,
                defaultValue: 6.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Anchor",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "DepthLimit",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "Face",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "FrameWidth",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "Spin",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "TabCount",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "TabThickness",
                table: "Setups");

            migrationBuilder.DropColumn(
                name: "TabWidth",
                table: "Setups");
        }
    }
}
