using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;

using Xunit;

namespace ThreeCTool.Data.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "3ctool-tests-" + Guid.NewGuid().ToString("N"));

    private string LibraryPath => Path.Combine(_folder, "library.db");

    private string ProjectFolder => Path.Combine(_folder, "project");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task OpenLibrary_SeedsBuiltInsOnce()
    {
        await using (await LibraryDatabase.OpenAsync(LibraryPath))
        {
        }

        await using var db = await LibraryDatabase.OpenAsync(LibraryPath);
        Assert.Equal(1, await db.MachineProfiles.CountAsync());
        Assert.Equal(2, await db.Toolheads.CountAsync());
        Assert.Equal(4, await db.Collets.CountAsync());
        Assert.Equal(14, await db.Materials.CountAsync());
        Assert.Equal(10, await db.Filaments.CountAsync());
        Assert.Equal(3, await db.PrintProfiles.CountAsync());
        Assert.Equal(0, await db.Tools.CountAsync());
    }

    [Fact]
    public async Task DeletedBuiltIn_IsNotReseeded()
    {
        var softwood = BuiltInLibrary.IdFor("material:softwood");
        await using (var db = await LibraryDatabase.OpenAsync(LibraryPath))
        {
            await db.Materials.Where(m => m.Id == softwood).ExecuteDeleteAsync();
        }

        await using var reopened = await LibraryDatabase.OpenAsync(LibraryPath);
        Assert.False(await reopened.Materials.AnyAsync(m => m.Id == softwood));
    }

    [Fact]
    public async Task MachineProfile_RoundTripsOwnedJson()
    {
        var expected = BuiltInLibrary.DefaultMachine();
        await using (await LibraryDatabase.OpenAsync(LibraryPath))
        {
        }

        await using var db = await LibraryDatabase.OpenAsync(LibraryPath);
        var machine = await db.MachineProfiles.Include(m => m.Toolheads).Include(m => m.Collets).SingleAsync();

        Assert.Equal(expected.Axes.Count, machine.Axes.Count);
        Assert.Equal(1300, machine.GetAxis(AxisName.X)!.MaxTravel);
        Assert.Equal(2, machine.GetAxis(AxisName.Z)!.MotorCount);
        Assert.Equal(expected.Spindle.PowerCurve.Count, machine.Spindle.PowerCurve.Count);
        Assert.Equal(2200, machine.Spindle.PowerAt(24000), 6);

        var fdm = machine.Toolheads.Single(t => t.Type == ToolheadType.Fdm);
        Assert.Equal(OperatorConfirmations.HeadMounted | OperatorConfirmations.CutterRemoved, fdm.RequiredConfirmations);
        Assert.NotNull(fdm.Fdm);
        Assert.Equal(4, fdm.CollisionModel.Count);
        Assert.Equal(600, machine.GetAxis(AxisName.Z)!.MaxTravel);
        var spindle = Assert.Single(machine.Toolheads.Single(t => t.Type == ToolheadType.Spindle).CollisionModel);
        Assert.Equal((37.5, 25.0, 50.0), (spindle.Radius, spindle.BottomZ, spindle.Height));
        Assert.Equal(4, machine.Collets.Count);
        Assert.All(machine.Collets, c => Assert.Equal((20.0, 25.0), (Assert.Single(c.Holder).Diameter, c.Holder[0].Length)));
        Assert.Equal(8, machine.Collets.Max(c => c.ShankDiameter));
    }

    [Fact]
    public async Task Material_RoundTripsNestedCuttingData()
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath);
        db.ChangeTracker.Clear();
        var aluminium = await db.Materials.SingleAsync(m => m.Id == BuiltInLibrary.IdFor("material:al-6061"));
        var carbide = aluminium.CuttingData.Single(c => c.ToolMaterial == ToolMaterial.Carbide);

        Assert.Equal(MaterialCategory.Aluminium, aluminium.Category);
        Assert.Equal(5, carbide.ChipLoad.Count);
        Assert.Equal(0.04, carbide.ChipLoad.Single(p => p.Diameter == 6).ChipLoad, 9);
        Assert.Contains(aluminium.CuttingData, c => c.ToolMaterial == ToolMaterial.Hss);

        var composite = await db.Materials.SingleAsync(m => m.Id == BuiltInLibrary.IdFor("material:cf-g10"));
        Assert.DoesNotContain(composite.CuttingData, c => c.ToolMaterial == ToolMaterial.Hss);
    }

    [Fact]
    public async Task Project_SetupWithOperations_RoundTripsAndCascades()
    {
        var setup = new Setup { Name = "Top", WorkOffset = WorkOffset.G55 };
        setup.Operations.Add(new Operation
        {
            SetupId = setup.Id,
            Name = "Adaptive",
            Strategy = StrategyType.AdaptiveRoughing,
            Parameters = new OperationParameters { Stepover = 2.5, Entry = EntryMode.Ramp },
            Conditions = new CuttingConditions { Rpm = 18000, FeedRate = 2400 },
        });

        await using (var db = await ProjectDatabase.OpenAsync(ProjectFolder))
        {
            db.Projects.Add(new Project { Name = "Test", Tolerances = Tolerances.ForScale(2) });
            db.Setups.Add(setup);
            await db.SaveChangesAsync();
        }

        await using (var db = await ProjectDatabase.OpenAsync(ProjectFolder))
        {
            var project = await db.Projects.SingleAsync();
            Assert.Equal(0.1, project.Tolerances.RoughingTolerance, 9);

            var loaded = await db.Setups.Include(s => s.Operations).SingleAsync();
            Assert.Equal(WorkOffset.G55, loaded.WorkOffset);
            var operation = Assert.Single(loaded.Operations);
            Assert.Equal(2.5, operation.Parameters.Stepover);
            Assert.Equal(EntryMode.Ramp, operation.Parameters.Entry);
            Assert.Equal(18000, operation.Conditions!.Rpm);

            db.Setups.Remove(loaded);
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Operations.CountAsync());
        }
    }

    [Fact]
    public async Task Snapshot_DetectsLibraryChangesAndDeletions()
    {
        var tool = new Tool { Name = "6 mm flat", Type = ToolType.FlatEnd, Diameter = 6, FluteCount = 2 };
        var materialId = BuiltInLibrary.IdFor("material:hardwood");
        var machineId = BuiltInLibrary.IdFor("machine:default");

        await using var library = await LibraryDatabase.OpenAsync(LibraryPath);
        library.Tools.Add(tool);
        await library.SaveChangesAsync();

        await using var project = await ProjectDatabase.OpenAsync(ProjectFolder);
        await ProjectSnapshot.CaptureMachineAsync(library, project, machineId);
        await ProjectSnapshot.CaptureMaterialAsync(library, project, materialId);
        await ProjectSnapshot.CaptureToolsAsync(library, project, [tool.Id]);
        Assert.Empty(await ProjectSnapshot.CompareAsync(library, project));

        var material = await library.Materials.SingleAsync(m => m.Id == materialId);
        material.CuttingData[0].ChipLoad[2].ChipLoad = 0.16;
        var spindle = await library.Toolheads.SingleAsync(t => t.Id == BuiltInLibrary.IdFor("toolhead:spindle"));
        spindle.OffsetZ = 12.5;
        library.Tools.Remove(tool);
        await library.SaveChangesAsync();
        library.ChangeTracker.Clear();

        var differences = await ProjectSnapshot.CompareAsync(library, project);
        Assert.Contains(differences, d => d.Id == materialId && d.Kind == SnapshotDifferenceKind.ChangedInLibrary);
        Assert.Contains(differences, d => d.Id == machineId && d.Kind == SnapshotDifferenceKind.ChangedInLibrary);
        Assert.Contains(differences, d => d.Id == tool.Id && d.Kind == SnapshotDifferenceKind.MissingFromLibrary);
        Assert.Equal(3, differences.Count);

        await ProjectSnapshot.CaptureMaterialAsync(library, project, materialId);
        await ProjectSnapshot.CaptureMachineAsync(library, project, machineId);
        var remaining = Assert.Single(await ProjectSnapshot.CompareAsync(library, project));
        Assert.Equal(tool.Id, remaining.Id);

        var snapshotMaterial = await project.Materials.SingleAsync();
        Assert.Equal(0.16, snapshotMaterial.CuttingData[0].ChipLoad[2].ChipLoad, 9);
        Assert.Equal(2, await project.Toolheads.CountAsync());
    }

    [Fact]
    public void BuiltInIds_AreStableVersion5Style()
    {
        var id = BuiltInLibrary.IdFor("material:mdf");
        Assert.Equal(id, BuiltInLibrary.IdFor("material:mdf"));
        Assert.NotEqual(id, BuiltInLibrary.IdFor("material:hdpe"));
        Assert.Equal(5, id.Version);
    }
}
