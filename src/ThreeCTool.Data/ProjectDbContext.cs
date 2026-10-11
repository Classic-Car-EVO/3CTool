using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Cutting;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Printing;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Data;

/// <summary>
/// Per-project <c>project.db</c>. Holds the project plus a snapshot of the machine, tools, material,
/// filament and print profile it uses. Snapshot references are plain ids (no FKs) so snapshots can be replaced.
/// </summary>
public sealed class ProjectDbContext(DbContextOptions<ProjectDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectTool> ProjectTools => Set<ProjectTool>();

    public DbSet<PartModel> PartModels => Set<PartModel>();

    public DbSet<Stock> Stocks => Set<Stock>();

    public DbSet<KeepOutZone> KeepOutZones => Set<KeepOutZone>();

    public DbSet<Setup> Setups => Set<Setup>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<PrintOperation> PrintOperations => Set<PrintOperation>();

    public DbSet<ToolpathResult> ToolpathResults => Set<ToolpathResult>();

    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();

    public DbSet<PlyBook> PlyBooks => Set<PlyBook>();

    public DbSet<Ply> Plies => Set<Ply>();

    public DbSet<FlatPattern> FlatPatterns => Set<FlatPattern>();

    public DbSet<NestLayout> NestLayouts => Set<NestLayout>();

    public DbSet<MachineProfile> MachineProfiles => Set<MachineProfile>();

    public DbSet<Toolhead> Toolheads => Set<Toolhead>();

    public DbSet<Collet> Collets => Set<Collet>();

    public DbSet<Tool> Tools => Set<Tool>();

    public DbSet<Material> Materials => Set<Material>();

    public DbSet<Filament> Filaments => Set<Filament>();

    public DbSet<PrintProfile> PrintProfiles => Set<PrintProfile>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        SharedModel.ConfigureConventions(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SharedModel.ConfigureLibraryEntities(modelBuilder);

        modelBuilder.Entity<Project>(e => e.OwnsOne(p => p.Tolerances, o => o.ToJson()));
        modelBuilder.Entity<ProjectTool>(e => e.HasIndex(t => t.ToolId).IsUnique());
        modelBuilder.Entity<PartModel>(e =>
        {
            e.OwnsMany(p => p.Transforms, o => o.ToJson());
            e.OwnsOne(p => p.RepairReport, o => o.ToJson());
        });
        modelBuilder.Entity<Stock>();
        modelBuilder.Entity<KeepOutZone>(e => e.OwnsOne(k => k.Solid, o => o.ToJson()));
        modelBuilder.Entity<Setup>(e =>
        {
            e.HasMany(s => s.Operations).WithOne().HasForeignKey(o => o.SetupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.PrintOperations).WithOne().HasForeignKey(o => o.SetupId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Operation>(e =>
        {
            e.OwnsOne(o => o.Parameters, o => o.ToJson());
            e.OwnsOne(o => o.Conditions, o => o.ToJson());
        });
        modelBuilder.Entity<PrintOperation>();
        modelBuilder.Entity<ToolpathResult>(e => e.HasIndex(r => r.OperationId));
        modelBuilder.Entity<GcodeFile>(e => e.HasIndex(g => g.SetupId));
        modelBuilder.Entity<PlyBook>(e => e.HasMany(b => b.Plies).WithOne().HasForeignKey(p => p.PlyBookId).OnDelete(DeleteBehavior.Cascade));
        modelBuilder.Entity<FlatPattern>(e => e.OwnsMany(f => f.Contours, o =>
        {
            o.ToJson();
            o.OwnsMany(c => c.Points);
        }));
        modelBuilder.Entity<NestLayout>(e => e.OwnsMany(n => n.Placements, o => o.ToJson()));
    }
}
