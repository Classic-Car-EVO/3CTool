using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Printing;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Data;

/// <summary>Mapping shared by the library DB and the project DB snapshot tables.</summary>
internal static class SharedModel
{
    public static void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>();
    }

    public static void ConfigureLibraryEntities(ModelBuilder builder)
    {
        builder.Entity<MachineProfile>(e =>
        {
            e.OwnsOne(m => m.Controller, o => o.ToJson());
            e.OwnsMany(m => m.Axes, o => o.ToJson());
            e.OwnsOne(m => m.Spindle, o =>
            {
                o.ToJson();
                o.OwnsMany(s => s.PowerCurve);
            });
            e.OwnsOne(m => m.RotaryAxis, o =>
            {
                o.ToJson();
                o.OwnsMany(r => r.Centers);
            });
            e.OwnsOne(m => m.Probe, o => o.ToJson());
            e.OwnsOne(m => m.Coolant, o => o.ToJson());
            e.OwnsOne(m => m.Connection, o => o.ToJson());
            e.HasMany(m => m.Toolheads).WithOne().HasForeignKey(t => t.MachineProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(m => m.Collets).WithOne().HasForeignKey(c => c.MachineProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Toolhead>(e =>
        {
            e.OwnsMany(t => t.CollisionModel, o => o.ToJson());
            e.OwnsOne(t => t.Fdm, o => o.ToJson());
            e.OwnsOne(t => t.Knife, o => o.ToJson());
        });

        builder.Entity<Collet>(e => e.OwnsMany(c => c.Holder, o => o.ToJson()));

        builder.Entity<Tool>();

        builder.Entity<Material>(e => e.OwnsMany(m => m.CuttingData, o =>
        {
            o.ToJson();
            o.OwnsMany(c => c.ChipLoad);
        }));

        builder.Entity<Filament>();
        builder.Entity<PrintProfile>();
    }
}
