using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Library
{
    /// <inheritdoc />
    public partial class PairedFeeders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeederRunId",
                table: "JobFeedback",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PairedFeeders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeederId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    PairingId = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PairedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairedFeeders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobFeedback_FeederRunId",
                table: "JobFeedback",
                column: "FeederRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PairedFeeders_FeederId",
                table: "PairedFeeders",
                column: "FeederId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairedFeeders");

            migrationBuilder.DropIndex(
                name: "IX_JobFeedback_FeederRunId",
                table: "JobFeedback");

            migrationBuilder.DropColumn(
                name: "FeederRunId",
                table: "JobFeedback");
        }
    }
}
