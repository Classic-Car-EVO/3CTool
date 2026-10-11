using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCTool.Data.Migrations.Project
{
    /// <inheritdoc />
    public partial class InitialProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "FlatPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Contours = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlatPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GcodeFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Head = table.Column<string>(type: "TEXT", nullable: false),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolheadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Rpm = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedSeconds = table.Column<double>(type: "REAL", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcodeFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KeepOutZones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Attachment = table.Column<string>(type: "TEXT", nullable: false),
                    SetupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Solid = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeepOutZones", x => x.Id);
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
                name: "NestLayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SheetWidth = table.Column<double>(type: "REAL", nullable: false),
                    SheetLength = table.Column<double>(type: "REAL", nullable: false),
                    Placements = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NestLayouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ImportUnit = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    RepairReport = table.Column<string>(type: "TEXT", nullable: true),
                    Transforms = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlyBooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlyBooks", x => x.Id);
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
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaterialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FilamentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrintProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Goal = table.Column<string>(type: "TEXT", nullable: false),
                    Tolerances = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Setups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Orientation = table.Column<string>(type: "TEXT", nullable: false),
                    IndexAngle = table.Column<double>(type: "REAL", nullable: false),
                    RotaryCenterName = table.Column<string>(type: "TEXT", nullable: true),
                    WorkOffset = table.Column<string>(type: "TEXT", nullable: false),
                    WcsReference = table.Column<string>(type: "TEXT", nullable: false),
                    WcsOffsetX = table.Column<double>(type: "REAL", nullable: false),
                    WcsOffsetY = table.Column<double>(type: "REAL", nullable: false),
                    WcsOffsetZ = table.Column<double>(type: "REAL", nullable: false),
                    ProbeMethod = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Setups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    OriginX = table.Column<double>(type: "REAL", nullable: false),
                    OriginY = table.Column<double>(type: "REAL", nullable: false),
                    OriginZ = table.Column<double>(type: "REAL", nullable: false),
                    SizeX = table.Column<double>(type: "REAL", nullable: false),
                    SizeY = table.Column<double>(type: "REAL", nullable: false),
                    SizeZ = table.Column<double>(type: "REAL", nullable: false),
                    Diameter = table.Column<double>(type: "REAL", nullable: false),
                    Length = table.Column<double>(type: "REAL", nullable: false),
                    MeshPath = table.Column<string>(type: "TEXT", nullable: true),
                    MeshSha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolpathResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputHash = table.Column<string>(type: "TEXT", nullable: false),
                    CachePath = table.Column<string>(type: "TEXT", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    MoveCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CutLength = table.Column<double>(type: "REAL", nullable: false),
                    RapidLength = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedSeconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolpathResults", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "Plies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlyBookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MaterialName = table.Column<string>(type: "TEXT", nullable: false),
                    FiberOrientationDegrees = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    FlatPatternId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plies_PlyBooks_PlyBookId",
                        column: x => x.PlyBookId,
                        principalTable: "PlyBooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    ToolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Conditions = table.Column<string>(type: "TEXT", nullable: true),
                    Parameters = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Operations_Setups_SetupId",
                        column: x => x.SetupId,
                        principalTable: "Setups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrintOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    VolumeSource = table.Column<string>(type: "TEXT", nullable: false),
                    PartModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorePartModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SelectedFaces = table.Column<string>(type: "TEXT", nullable: false),
                    SkinThickness = table.Column<double>(type: "REAL", nullable: false),
                    PrintProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilamentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolheadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Substrate = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintOperations_Setups_SetupId",
                        column: x => x.SetupId,
                        principalTable: "Setups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Collets_MachineProfileId",
                table: "Collets",
                column: "MachineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_SetupId",
                table: "GcodeFiles",
                column: "SetupId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_SetupId",
                table: "Operations",
                column: "SetupId");

            migrationBuilder.CreateIndex(
                name: "IX_Plies_PlyBookId",
                table: "Plies",
                column: "PlyBookId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintOperations_SetupId",
                table: "PrintOperations",
                column: "SetupId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTools_ToolId",
                table: "ProjectTools",
                column: "ToolId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Toolheads_MachineProfileId",
                table: "Toolheads",
                column: "MachineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolpathResults_OperationId",
                table: "ToolpathResults",
                column: "OperationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Collets");

            migrationBuilder.DropTable(
                name: "Filaments");

            migrationBuilder.DropTable(
                name: "FlatPatterns");

            migrationBuilder.DropTable(
                name: "GcodeFiles");

            migrationBuilder.DropTable(
                name: "KeepOutZones");

            migrationBuilder.DropTable(
                name: "Materials");

            migrationBuilder.DropTable(
                name: "NestLayouts");

            migrationBuilder.DropTable(
                name: "Operations");

            migrationBuilder.DropTable(
                name: "PartModels");

            migrationBuilder.DropTable(
                name: "Plies");

            migrationBuilder.DropTable(
                name: "PrintOperations");

            migrationBuilder.DropTable(
                name: "PrintProfiles");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "ProjectTools");

            migrationBuilder.DropTable(
                name: "Stocks");

            migrationBuilder.DropTable(
                name: "Toolheads");

            migrationBuilder.DropTable(
                name: "ToolpathResults");

            migrationBuilder.DropTable(
                name: "Tools");

            migrationBuilder.DropTable(
                name: "PlyBooks");

            migrationBuilder.DropTable(
                name: "Setups");

            migrationBuilder.DropTable(
                name: "MachineProfiles");
        }
    }
}
