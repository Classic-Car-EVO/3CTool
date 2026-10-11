using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Library
{
    /// <inheritdoc />
    public partial class InitialLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaterialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    ChipLoadFactor = table.Column<double>(type: "REAL", nullable: false),
                    SurfaceSpeedFactor = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedCorrections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Filaments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Diameter = table.Column<double>(type: "REAL", nullable: false),
                    NozzleTemperatureMin = table.Column<double>(type: "REAL", nullable: false),
                    NozzleTemperatureMax = table.Column<double>(type: "REAL", nullable: false),
                    BedTemperatureMin = table.Column<double>(type: "REAL", nullable: false),
                    BedTemperatureMax = table.Column<double>(type: "REAL", nullable: false),
                    Density = table.Column<double>(type: "REAL", nullable: false),
                    MaxVolumetricSpeed = table.Column<double>(type: "REAL", nullable: false),
                    FanMinPercent = table.Column<double>(type: "REAL", nullable: false),
                    FanMaxPercent = table.Column<double>(type: "REAL", nullable: false),
                    FanOffFirstLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    ShrinkagePercent = table.Column<double>(type: "REAL", nullable: false),
                    Abrasive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Filaments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: false),
                    GcodeSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    MaterialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FinishRating = table.Column<int>(type: "INTEGER", nullable: true),
                    ChatterRating = table.Column<int>(type: "INTEGER", nullable: true),
                    WearRating = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    FeedOverrides = table.Column<string>(type: "TEXT", nullable: true),
                    RpmChanges = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobFeedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MachineProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SafeZMachine = table.Column<double>(type: "REAL", nullable: false),
                    ArcFitting = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArcFittingTolerance = table.Column<double>(type: "REAL", nullable: false),
                    Axes = table.Column<string>(type: "TEXT", nullable: true),
                    Connection = table.Column<string>(type: "TEXT", nullable: false),
                    Controller = table.Column<string>(type: "TEXT", nullable: false),
                    Coolant = table.Column<string>(type: "TEXT", nullable: false),
                    Probe = table.Column<string>(type: "TEXT", nullable: false),
                    RotaryAxis = table.Column<string>(type: "TEXT", nullable: false),
                    Spindle = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Materials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Hardness = table.Column<string>(type: "TEXT", nullable: true),
                    SpecificCuttingForce = table.Column<double>(type: "REAL", nullable: false),
                    KienzleExponent = table.Column<double>(type: "REAL", nullable: false),
                    ToleranceScale = table.Column<double>(type: "REAL", nullable: false),
                    MaxDocFactor = table.Column<double>(type: "REAL", nullable: false),
                    MaxWocFactor = table.Column<double>(type: "REAL", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CuttingData = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Materials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrintProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NozzleDiameter = table.Column<double>(type: "REAL", nullable: false),
                    LayerHeight = table.Column<double>(type: "REAL", nullable: false),
                    FirstLayerHeight = table.Column<double>(type: "REAL", nullable: false),
                    AdaptiveLayerHeight = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinLayerHeight = table.Column<double>(type: "REAL", nullable: false),
                    MaxLayerHeight = table.Column<double>(type: "REAL", nullable: false),
                    LineWidth = table.Column<double>(type: "REAL", nullable: false),
                    FirstLayerLineWidth = table.Column<double>(type: "REAL", nullable: false),
                    Perimeters = table.Column<int>(type: "INTEGER", nullable: false),
                    Seam = table.Column<string>(type: "TEXT", nullable: false),
                    TopLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    BottomLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    Infill = table.Column<string>(type: "TEXT", nullable: false),
                    InfillDensity = table.Column<double>(type: "REAL", nullable: false),
                    ExtrusionMultiplier = table.Column<double>(type: "REAL", nullable: false),
                    PerimeterSpeed = table.Column<double>(type: "REAL", nullable: false),
                    ExternalPerimeterSpeed = table.Column<double>(type: "REAL", nullable: false),
                    InfillSpeed = table.Column<double>(type: "REAL", nullable: false),
                    TopSolidSpeed = table.Column<double>(type: "REAL", nullable: false),
                    SupportSpeed = table.Column<double>(type: "REAL", nullable: false),
                    BridgeSpeed = table.Column<double>(type: "REAL", nullable: false),
                    FirstLayerSpeed = table.Column<double>(type: "REAL", nullable: false),
                    TravelSpeed = table.Column<double>(type: "REAL", nullable: false),
                    RetractLength = table.Column<double>(type: "REAL", nullable: false),
                    RetractSpeed = table.Column<double>(type: "REAL", nullable: false),
                    DeretractSpeed = table.Column<double>(type: "REAL", nullable: false),
                    RetractMinTravel = table.Column<double>(type: "REAL", nullable: false),
                    WipeDistance = table.Column<double>(type: "REAL", nullable: false),
                    ZHop = table.Column<double>(type: "REAL", nullable: false),
                    Support = table.Column<string>(type: "TEXT", nullable: false),
                    SupportOverhangAngle = table.Column<double>(type: "REAL", nullable: false),
                    SupportInterfaceLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    SupportZDistance = table.Column<double>(type: "REAL", nullable: false),
                    SupportXyDistance = table.Column<double>(type: "REAL", nullable: false),
                    DetectBridges = table.Column<bool>(type: "INTEGER", nullable: false),
                    BridgeFlowRatio = table.Column<double>(type: "REAL", nullable: false),
                    Adhesion = table.Column<string>(type: "TEXT", nullable: false),
                    SkirtLoops = table.Column<int>(type: "INTEGER", nullable: false),
                    SkirtDistance = table.Column<double>(type: "REAL", nullable: false),
                    BrimWidth = table.Column<double>(type: "REAL", nullable: false),
                    RaftLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    Ironing = table.Column<bool>(type: "INTEGER", nullable: false),
                    IroningSpacing = table.Column<double>(type: "REAL", nullable: false),
                    IroningFlow = table.Column<double>(type: "REAL", nullable: false),
                    IroningSpeed = table.Column<double>(type: "REAL", nullable: false),
                    MinLayerTime = table.Column<double>(type: "REAL", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Diameter = table.Column<double>(type: "REAL", nullable: false),
                    CornerRadius = table.Column<double>(type: "REAL", nullable: false),
                    TipAngle = table.Column<double>(type: "REAL", nullable: false),
                    TaperAngle = table.Column<double>(type: "REAL", nullable: false),
                    TipDiameter = table.Column<double>(type: "REAL", nullable: false),
                    FluteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FluteLength = table.Column<double>(type: "REAL", nullable: false),
                    ShankDiameter = table.Column<double>(type: "REAL", nullable: false),
                    OverallLength = table.Column<double>(type: "REAL", nullable: false),
                    Stickout = table.Column<double>(type: "REAL", nullable: false),
                    Material = table.Column<string>(type: "TEXT", nullable: false),
                    Coating = table.Column<string>(type: "TEXT", nullable: true),
                    HelixAngle = table.Column<double>(type: "REAL", nullable: false),
                    MaxDepthOfCut = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    ProductCode = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolWearLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoggedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeMinutes = table.Column<double>(type: "REAL", nullable: false),
                    CutLength = table.Column<double>(type: "REAL", nullable: false),
                    JobFeedbackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolWearLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Collets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ShankDiameter = table.Column<double>(type: "REAL", nullable: false),
                    Holder = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collets_MachineProfiles_MachineProfileId",
                        column: x => x.MachineProfileId,
                        principalTable: "MachineProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Toolheads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Mount = table.Column<string>(type: "TEXT", nullable: false),
                    OffsetX = table.Column<double>(type: "REAL", nullable: false),
                    OffsetY = table.Column<double>(type: "REAL", nullable: false),
                    OffsetZ = table.Column<double>(type: "REAL", nullable: false),
                    OffsetCalibratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UsesBAxis = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredConfirmations = table.Column<string>(type: "TEXT", nullable: false),
                    CollisionModel = table.Column<string>(type: "TEXT", nullable: true),
                    Fdm = table.Column<string>(type: "TEXT", nullable: true),
                    Knife = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Toolheads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Toolheads_MachineProfiles_MachineProfileId",
                        column: x => x.MachineProfileId,
                        principalTable: "MachineProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Collets_MachineProfileId",
                table: "Collets",
                column: "MachineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedCorrections_MaterialId_ToolId_Strategy",
                table: "FeedCorrections",
                columns: new[] { "MaterialId", "ToolId", "Strategy" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Toolheads_MachineProfileId",
                table: "Toolheads",
                column: "MachineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolWearLogs_ToolId",
                table: "ToolWearLogs",
                column: "ToolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Collets");

            migrationBuilder.DropTable(
                name: "FeedCorrections");

            migrationBuilder.DropTable(
                name: "Filaments");

            migrationBuilder.DropTable(
                name: "JobFeedback");

            migrationBuilder.DropTable(
                name: "Materials");

            migrationBuilder.DropTable(
                name: "PrintProfiles");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Toolheads");

            migrationBuilder.DropTable(
                name: "Tools");

            migrationBuilder.DropTable(
                name: "ToolWearLogs");

            migrationBuilder.DropTable(
                name: "MachineProfiles");
        }
    }
}
