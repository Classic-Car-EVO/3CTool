using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Project
{
    /// <inheritdoc />
    public partial class StockTiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Piece",
                table: "ToolpathResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PieceName",
                table: "Stocks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PieceSizeX",
                table: "Stocks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PieceSizeY",
                table: "Stocks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PieceSizeZ",
                table: "Stocks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "SeamsX",
                table: "Stocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SeamsY",
                table: "Stocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SeamsZ",
                table: "Stocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "Piece",
                table: "GcodeFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Piece",
                table: "ToolpathResults");

            migrationBuilder.DropColumn(
                name: "PieceName",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "PieceSizeX",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "PieceSizeY",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "PieceSizeZ",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "SeamsX",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "SeamsY",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "SeamsZ",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "Piece",
                table: "GcodeFiles");
        }
    }
}
