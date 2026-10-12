using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Printing;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Data;

/// <summary>Central library: machines, tools, materials, filaments, print profiles and learned data.</summary>
public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<MachineProfile> MachineProfiles => Set<MachineProfile>();

    public DbSet<Toolhead> Toolheads => Set<Toolhead>();

    public DbSet<Collet> Collets => Set<Collet>();

    public DbSet<Tool> Tools => Set<Tool>();

    public DbSet<Material> Materials => Set<Material>();

    public DbSet<Filament> Filaments => Set<Filament>();

    public DbSet<PrintProfile> PrintProfiles => Set<PrintProfile>();

    public DbSet<FeedCorrection> FeedCorrections => Set<FeedCorrection>();

    public DbSet<ToolWearLog> ToolWearLogs => Set<ToolWearLog>();

    public DbSet<JobFeedback> JobFeedback => Set<JobFeedback>();

    public DbSet<LibrarySetting> Settings => Set<LibrarySetting>();

    public DbSet<PairedFeeder> PairedFeeders => Set<PairedFeeder>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        SharedModel.ConfigureConventions(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SharedModel.ConfigureLibraryEntities(modelBuilder);

        modelBuilder.Entity<FeedCorrection>(e => e.HasIndex(f => new { f.MaterialId, f.ToolId, f.Strategy }).IsUnique());
        modelBuilder.Entity<ToolWearLog>(e => e.HasIndex(w => w.ToolId));
        modelBuilder.Entity<JobFeedback>(e =>
        {
            e.OwnsMany(f => f.FeedOverrides, o => o.ToJson());
            e.OwnsMany(f => f.RpmChanges, o => o.ToJson());
            e.HasIndex(f => f.FeederRunId).IsUnique();
        });
        modelBuilder.Entity<PairedFeeder>(e => e.HasIndex(f => f.FeederId).IsUnique());
        modelBuilder.Entity<LibrarySetting>().HasKey(s => s.Key);
    }
}

public sealed class LibrarySetting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
