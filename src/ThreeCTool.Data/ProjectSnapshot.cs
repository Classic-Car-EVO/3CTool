using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Printing;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Data;

public enum SnapshotDifferenceKind
{
    ChangedInLibrary,
    MissingFromLibrary,
}

public sealed record SnapshotDifference(string EntityType, Guid Id, string Name, SnapshotDifferenceKind Kind);

/// <summary>Copies library records into a project's snapshot tables and reports where they have since diverged.</summary>
public static class ProjectSnapshot
{
    public static async Task CaptureMachineAsync(LibraryDbContext library, ProjectDbContext project, Guid machineId, CancellationToken cancellationToken = default)
    {
        var machine = await LoadMachineAsync(library.MachineProfiles, machineId, cancellationToken)
            ?? throw new KeyNotFoundException($"Machine profile {machineId} not found in the library.");
        await ReplaceAsync(project, project.MachineProfiles.Where(m => m.Id == machineId), machine, cancellationToken);
    }

    public static async Task CaptureToolsAsync(LibraryDbContext library, ProjectDbContext project, IReadOnlyCollection<Guid> toolIds, CancellationToken cancellationToken = default)
    {
        var tools = await library.Tools.AsNoTracking().Where(t => toolIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (tools.Count != toolIds.Count)
        {
            throw new KeyNotFoundException("One or more tools were not found in the library.");
        }

        foreach (var tool in tools)
        {
            await ReplaceAsync(project, project.Tools.Where(t => t.Id == tool.Id), tool, cancellationToken);
        }
    }

    public static Task CaptureMaterialAsync(LibraryDbContext library, ProjectDbContext project, Guid materialId, CancellationToken cancellationToken = default) =>
        CaptureAsync(library.Materials, project, project.Materials, materialId, cancellationToken);

    public static Task CaptureFilamentAsync(LibraryDbContext library, ProjectDbContext project, Guid filamentId, CancellationToken cancellationToken = default) =>
        CaptureAsync(library.Filaments, project, project.Filaments, filamentId, cancellationToken);

    public static Task CapturePrintProfileAsync(LibraryDbContext library, ProjectDbContext project, Guid printProfileId, CancellationToken cancellationToken = default) =>
        CaptureAsync(library.PrintProfiles, project, project.PrintProfiles, printProfileId, cancellationToken);

    public static async Task<IReadOnlyList<SnapshotDifference>> CompareAsync(LibraryDbContext library, ProjectDbContext project, CancellationToken cancellationToken = default)
    {
        var differences = new List<SnapshotDifference>();

        foreach (var snapshot in await project.MachineProfiles.AsNoTracking().Include(m => m.Toolheads).Include(m => m.Collets).ToListAsync(cancellationToken))
        {
            var current = await LoadMachineAsync(library.MachineProfiles, snapshot.Id, cancellationToken);
            Compare(differences, nameof(MachineProfile), snapshot.Id, snapshot.Name, Normalize(snapshot), current is null ? null : Normalize(current));
        }

        await CompareSetAsync(differences, project.Tools, library.Tools, t => t.Id, t => t.Name, cancellationToken);
        await CompareSetAsync(differences, project.Materials, library.Materials, m => m.Id, m => m.Name, cancellationToken);
        await CompareSetAsync(differences, project.Filaments, library.Filaments, f => f.Id, f => f.Name, cancellationToken);
        await CompareSetAsync(differences, project.PrintProfiles, library.PrintProfiles, p => p.Id, p => p.Name, cancellationToken);
        return differences;
    }

    private static async Task<MachineProfile?> LoadMachineAsync(IQueryable<MachineProfile> machines, Guid id, CancellationToken cancellationToken) =>
        await machines.AsNoTracking().Include(m => m.Toolheads).Include(m => m.Collets).SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    private static async Task CaptureAsync<T>(DbSet<T> source, ProjectDbContext project, DbSet<T> target, Guid id, CancellationToken cancellationToken)
        where T : class
    {
        var entity = await source.AsNoTracking().SingleOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken)
            ?? throw new KeyNotFoundException($"{typeof(T).Name} {id} not found in the library.");
        await ReplaceAsync(project, target.Where(e => EF.Property<Guid>(e, "Id") == id), entity, cancellationToken);
    }

    // Delete-then-insert keeps owned JSON columns and child rows exactly equal to the library copy.
    private static async Task ReplaceAsync<T>(ProjectDbContext project, IQueryable<T> existing, T entity, CancellationToken cancellationToken)
        where T : class
    {
        await using var transaction = await project.Database.BeginTransactionAsync(cancellationToken);
        await existing.ExecuteDeleteAsync(cancellationToken);
        project.ChangeTracker.Clear();
        project.Add(entity);
        await project.SaveChangesAsync(cancellationToken);
        project.ChangeTracker.Clear();
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CompareSetAsync<T>(
        List<SnapshotDifference> differences,
        DbSet<T> snapshots,
        DbSet<T> library,
        Func<T, Guid> getId,
        Func<T, string> getName,
        CancellationToken cancellationToken)
        where T : class
    {
        foreach (var snapshot in await snapshots.AsNoTracking().ToListAsync(cancellationToken))
        {
            var id = getId(snapshot);
            var current = await library.AsNoTracking().SingleOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken);
            Compare(differences, typeof(T).Name, id, getName(snapshot), snapshot, current);
        }
    }

    private static void Compare<T>(List<SnapshotDifference> differences, string type, Guid id, string name, T snapshot, T? current)
        where T : class
    {
        if (current is null)
        {
            differences.Add(new SnapshotDifference(type, id, name, SnapshotDifferenceKind.MissingFromLibrary));
        }
        else if (JsonSerializer.Serialize(snapshot) != JsonSerializer.Serialize(current))
        {
            differences.Add(new SnapshotDifference(type, id, name, SnapshotDifferenceKind.ChangedInLibrary));
        }
    }

    private static MachineProfile Normalize(MachineProfile machine)
    {
        machine.Toolheads = [.. machine.Toolheads.OrderBy(t => t.Id)];
        machine.Collets = [.. machine.Collets.OrderBy(c => c.Id)];
        return machine;
    }
}
