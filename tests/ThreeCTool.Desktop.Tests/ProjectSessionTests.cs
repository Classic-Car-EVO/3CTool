using System.Numerics;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.Tests;

public sealed class ProjectSessionTests : IAsyncDisposable
{
    private readonly DesktopFixture fixture = new();

    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Create_SnapshotsLibraryAndSetsDefaults()
    {
        var machineId = await fixture.DefaultMachineIdAsync();
        var materialId = await fixture.MaterialIdAsync();
        await using (var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "Bracket", machineId, materialId))
        {
            Assert.Equal("Bracket", session.Project.Name);
            Assert.Equal(machineId, session.Machine.Id);
            Assert.Equal(materialId, session.Material!.Id);
            Assert.Equal(StockType.Box, session.Stock.Type);
            var setup = Assert.Single(session.Setups);
            Assert.Equal(WorkOffset.G54, setup.WorkOffset);
            Assert.Empty(await session.CompareWithLibraryAsync());
        }

        Assert.True(ProjectSession.IsProjectFolder(fixture.ProjectFolder()));
        await Assert.ThrowsAsync<IOException>(() => ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "Again", machineId, null));
    }

    [Fact]
    public async Task LibraryChanges_AreReportedAndRefreshed()
    {
        var machineId = await fixture.DefaultMachineIdAsync();
        await using var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "P", machineId, null);
        var machine = (await fixture.Library.GetMachineAsync(machineId))!;
        machine.SafeZMachine = -9;
        await fixture.Library.SaveMachineAsync(machine);

        var difference = Assert.Single(await session.CompareWithLibraryAsync());
        Assert.Equal(SnapshotDifferenceKind.ChangedInLibrary, difference.Kind);
        await session.RefreshSnapshotAsync(difference);
        Assert.Equal(-9, session.Machine.SafeZMachine);
        Assert.Empty(await session.CompareWithLibraryAsync());
    }

    [Fact]
    public async Task Parts_ImportTransformUndoAndPersist()
    {
        var machineId = await fixture.DefaultMachineIdAsync();
        var stl = fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(1, 2, 0.5f));
        Guid partId;
        await using (var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "P", machineId, null))
        {
            var import = await session.ImportPartAsync(stl, LengthUnit.Inch);
            partId = import.Model.Id;
            Assert.False(import.UnitsDeclared);
            Assert.Equal(LengthUnit.Inch, import.Model.ImportUnit);
            Assert.StartsWith(ProjectSession.ModelsFolder + "/", import.Model.FilePath.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(PartGeometry.Resolve(session.Folder, import.Model.FilePath)));

            var size = (await session.MillPartAsync()).Bounds.Size;
            Assert.Equal(25.4f, size.X, 3);
            Assert.Equal(50.8f, size.Y, 3);

            await session.AddTransformAsync(import.Model, MeshTransforms.Translate(new Vector3(5, 0, 0)));
            await session.AddTransformAsync(import.Model, MeshTransforms.Scale(2));
            Assert.True(await session.UndoTransformAsync(import.Model));
            Assert.Equal(5f, (await session.MillPartAsync()).Bounds.Min.X, 3);

            await session.PlaceOnStockAsync(import.Model, StockAnchor.Top);
            var bounds = (await session.MillPartAsync()).Bounds;
            Assert.Equal(session.Stock.OriginZ + session.Stock.SizeZ, bounds.Max.Z, 3);
            Assert.Equal(session.Stock.OriginX + (session.Stock.SizeX / 2), bounds.Center.X, 3);

            var second = await session.ImportPartAsync(stl, LengthUnit.Millimeter);
            Assert.Equal(import.Model.FilePath, second.Model.FilePath);
            await session.RemovePartAsync(second.Model);
            Assert.True(File.Exists(PartGeometry.Resolve(session.Folder, import.Model.FilePath)));
        }

        await using (var reopened = await ProjectSession.OpenAsync(fixture.ProjectFolder(), fixture.LibraryPath))
        {
            var part = Assert.Single(reopened.Parts);
            Assert.Equal(partId, part.Id);
            Assert.Equal(2, part.Transforms.Count);
            Assert.Equal(reopened.Stock.OriginZ + reopened.Stock.SizeZ, (await reopened.MillPartAsync()).Bounds.Max.Z, 3);
            await reopened.ClearTransformsAsync(part);
            Assert.False(await reopened.UndoTransformAsync(part));
        }
    }

    [Fact]
    public async Task RotateAboutCenter_IsOneStep_AndOldHistoriesWithoutPivotStillLoad()
    {
        var machineId = await fixture.DefaultMachineIdAsync();
        var stl = fixture.WriteBoxStl("block.stl", new Vector3(10, 0, 0), new Vector3(30, 10, 4));
        await using (var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "P", machineId, null))
        {
            var model = (await session.ImportPartAsync(stl, LengthUnit.Millimeter)).Model;
            await session.RotateAboutCenterAsync(model, MeshTransforms.Rotate(Vector3.UnitZ, 90));
            var step = Assert.Single(model.Transforms);
            Assert.Equal((20.0, 5.0, 2.0), (step.PivotX, step.PivotY, step.PivotZ));
            var bounds = (await session.MillPartAsync()).Bounds;
            Assert.Equal((15f, -5f, 0f), (MathF.Round(bounds.Min.X, 3), MathF.Round(bounds.Min.Y, 3), MathF.Round(bounds.Min.Z, 3)));
            Assert.Equal((25f, 15f, 4f), (MathF.Round(bounds.Max.X, 3), MathF.Round(bounds.Max.Y, 3), MathF.Round(bounds.Max.Z, 3)));
            await session.AddTransformAsync(model, MeshTransforms.Translate(new Vector3(1, 0, 0)));
        }

        // Histories saved before pivots existed have no pivot keys: they load as rotations about the origin.
        var database = Path.Combine(fixture.ProjectFolder(), ProjectDatabase.FileName);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PartModels SET Transforms = json_remove(Transforms, '$[0].PivotX', '$[0].PivotY', '$[0].PivotZ', '$[1].PivotX', '$[1].PivotY', '$[1].PivotZ') WHERE Transforms LIKE '%PivotX%'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var reopened = await ProjectSession.OpenAsync(fixture.ProjectFolder(), fixture.LibraryPath);
        var steps = Assert.Single(reopened.Parts).Transforms;
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal((0.0, 0.0, 0.0), (s.PivotX, s.PivotY, s.PivotZ)));
        Assert.Equal(-9f, (await reopened.MillPartAsync()).Bounds.Min.X, 3);
    }

    [Fact]
    public async Task ModelPaths_CannotEscapeTheProject()
    {
        Assert.Throws<InvalidDataException>(() => PartGeometry.Resolve(fixture.Root, "../x.stl"));
        Assert.Throws<InvalidDataException>(() => PartGeometry.Resolve(fixture.Root, Path.GetFullPath("/etc/passwd")));

        var machineId = await fixture.DefaultMachineIdAsync();
        var stl = fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(10));
        await using var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "P", machineId, null);
        var import = await session.ImportPartAsync(stl, LengthUnit.Millimeter);
        File.AppendAllText(PartGeometry.Resolve(session.Folder, import.Model.FilePath), "x");
        Assert.Throws<InvalidDataException>(() => PartGeometry.Load(session.Folder, import.Model));
    }

    [Fact]
    public async Task SetupsAndOperations_AreOrderedAndGuardTools()
    {
        var tool = DesktopFixture.FlatEndMill();
        await fixture.Library.SaveToolAsync(tool);
        var machineId = await fixture.DefaultMachineIdAsync();
        await using var session = await ProjectSession.CreateAsync(fixture.ProjectFolder(), fixture.LibraryPath, "P", machineId, null);
        var first = session.Setups[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AddOperationAsync(first, StrategyType.ZLevelRoughing, tool.Id));

        await session.AddToolsAsync([tool.Id]);
        Assert.NotNull(session.ColletFor(session.FindTool(tool.Id)!));
        var rough = await session.AddOperationAsync(first, StrategyType.ZLevelRoughing, tool.Id);
        var finish = await session.AddOperationAsync(first, StrategyType.ParallelFinishing, tool.Id);
        var copy = await session.DuplicateOperationAsync(rough);
        Assert.Equal([rough.Id, copy.Id, finish.Id], session.Setups[0].Operations.Select(o => o.Id));
        await session.MoveOperationAsync(finish, -2);
        Assert.Equal([finish.Id, rough.Id, copy.Id], session.Setups[0].Operations.Select(o => o.Id));
        Assert.Equal([1, 2, 3], session.Setups[0].Operations.Select(o => o.Order));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RemoveToolAsync(tool.Id));

        var second = await session.AddSetupAsync(SetupOrientation.ThreeAxis);
        Assert.Equal(WorkOffset.G55, second.WorkOffset);
        await session.AddKeepOutAsync(new KeepOutZone
        {
            Attachment = KeepOutAttachment.Setup,
            SetupId = second.Id,
            Solid = new SimpleSolid { Shape = SolidShape.Box, SizeX = 10, SizeY = 10, Height = 5 },
        });
        await session.MoveSetupAsync(second, -1);
        Assert.Equal([second.Id, first.Id], session.Setups.Select(s => s.Id));
        await session.RemoveSetupAsync(second);
        Assert.Empty(session.KeepOuts);
        Assert.Equal(1, Assert.Single(session.Setups).Order);

        foreach (var op in session.Setups[0].Operations.ToList())
        {
            await session.RemoveOperationAsync(op);
        }

        await session.RemoveToolAsync(tool.Id);
        Assert.Empty(session.Tools);
    }
}
