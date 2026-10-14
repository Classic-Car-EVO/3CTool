using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;

namespace ThreeCTool.Desktop.Services;

/// <summary>Tool usage since the last replacement, from the wear log.</summary>
public sealed record ToolUsage(Guid ToolId, double RuntimeMinutes, double CutLength, int Runs, DateTimeOffset? LastReplaced, WearEventKind? LastEvent);

/// <summary>
/// Create/read/update/delete for the global library (machines with collets, tools, materials) plus the learned
/// feed corrections and job history. Every call uses its own short-lived context; returned entities are detached.
/// </summary>
public sealed class LibraryService(string libraryPath, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public string LibraryPath { get; } = libraryPath;

    public async Task<IReadOnlyList<MachineProfile>> ListMachinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var machines = await db.MachineProfiles.AsNoTracking().Include(m => m.Collets).Include(m => m.Toolheads)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. machines.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<MachineProfile?> GetMachineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await db.MachineProfiles.AsNoTracking().Include(m => m.Collets).Include(m => m.Toolheads)
            .SingleOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts or replaces the machine with its collets and toolheads (same ids keep project links valid).</summary>
    public async Task SaveMachineAsync(MachineProfile machine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        Validate(!string.IsNullOrWhiteSpace(machine.Name), "The machine needs a name.");
        foreach (var collet in machine.Collets)
        {
            collet.MachineProfileId = machine.Id;
            Validate(collet.ShankDiameter > 0, $"Collet '{collet.Name}' needs a shank diameter.");
        }

        foreach (var head in machine.Toolheads)
        {
            head.MachineProfileId = machine.Id;
        }

        var copy = EntityCopy.Clone(machine);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(db, db.MachineProfiles.Where(m => m.Id == copy.Id), copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMachineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        Validate(await db.MachineProfiles.CountAsync(cancellationToken).ConfigureAwait(false) > 1, "The library needs at least one machine.");
        await db.MachineProfiles.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copy with new ids for the machine, its collets and toolheads.</summary>
    public static MachineProfile Duplicate(MachineProfile machine, string name)
    {
        var copy = EntityCopy.Clone(machine);
        copy.Id = Guid.CreateVersion7();
        copy.Name = name;
        copy.Controller = new ControllerInfo();
        foreach (var collet in copy.Collets)
        {
            collet.Id = Guid.CreateVersion7();
            collet.MachineProfileId = copy.Id;
        }

        foreach (var head in copy.Toolheads)
        {
            head.Id = Guid.CreateVersion7();
            head.MachineProfileId = copy.Id;
        }

        return copy;
    }

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var tools = await db.Tools.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. tools.OrderBy(t => t.Diameter).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task SaveToolAsync(Tool tool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ValidateTool(tool);
        var copy = EntityCopy.Clone(tool);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(db, db.Tools.Where(t => t.Id == copy.Id), copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteToolAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await db.Tools.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateTool(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Validate(!string.IsNullOrWhiteSpace(tool.Name), "The tool needs a name.");
        Validate(tool.Diameter > 0, "Tool diameter must be positive.");
        Validate(tool.FluteCount > 0, "Flute count must be positive.");
        Validate(tool.Stickout > 0, "Stickout must be positive.");
        Validate(tool.FluteLength >= 0 && tool.ShankDiameter >= 0 && tool.OverallLength >= 0, "Lengths cannot be negative.");
        Validate(tool.OverallLength == 0 || tool.Stickout <= tool.OverallLength, "Stickout cannot exceed the overall length.");
        switch (tool.Type)
        {
            case ToolType.BullNose:
                Validate(tool.CornerRadius > 0 && tool.CornerRadius < tool.Diameter / 2, "Bull nose corner radius must be between 0 and half the diameter.");
                break;
            case ToolType.VBit or ToolType.Chamfer:
                Validate(tool.TipAngle is > 0 and < 180, "Tip angle must be between 0 and 180°.");
                break;
            case ToolType.TaperedBall:
                Validate(tool.TaperAngle is > 0 and < 90 && tool.TipDiameter > 0, "Tapered ball tools need a taper angle and a tip diameter.");
                break;
        }
    }

    public async Task<IReadOnlyList<Material>> ListMaterialsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var materials = await db.Materials.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. materials.OrderBy(m => m.Category).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task SaveMaterialAsync(Material material, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        Validate(!string.IsNullOrWhiteSpace(material.Name), "The material needs a name.");
        Validate(material.SpecificCuttingForce > 0, "Specific cutting force must be positive.");
        Validate(material.ToleranceScale > 0, "Tolerance scale must be positive.");
        Validate(material.MaxDocFactor > 0 && material.MaxWocFactor > 0, "DOC/WOC factors must be positive.");
        foreach (var data in material.CuttingData)
        {
            Validate(data.SurfaceSpeedMin > 0 && data.SurfaceSpeedMax >= data.SurfaceSpeedMin, $"{data.ToolMaterial}: surface speed range is invalid.");
            Validate(data.ChipLoad.Count > 0 && data.ChipLoad.All(p => p.Diameter > 0 && p.ChipLoad > 0), $"{data.ToolMaterial}: chip load table needs positive diameter/chip load points.");
        }

        var copy = EntityCopy.Clone(material);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(db, db.Materials.Where(m => m.Id == copy.Id), copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMaterialAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var material = await db.Materials.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false);
        Validate(material is not { IsBuiltIn: true }, "Built-in materials cannot be deleted; duplicate and edit them instead.");
        await db.StockSizes.Where(s => s.MaterialId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaterialId, (Guid?)null), cancellationToken).ConfigureAwait(false);
        await db.Materials.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StockSize>> ListStockSizesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var sizes = await db.StockSizes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. sizes.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task SaveStockSizeAsync(StockSize size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(size);
        Validate(!string.IsNullOrWhiteSpace(size.Name), "The stock size needs a name.");
        Validate(size.SizeX > 0 && size.SizeY > 0 && size.SizeZ > 0, "Length, width and thickness must be positive.");
        var copy = EntityCopy.Clone(size);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        Validate(copy.MaterialId is not { } materialId || await db.Materials.AnyAsync(m => m.Id == materialId, cancellationToken).ConfigureAwait(false), "The material of the stock size is not in the library.");
        await ReplaceAsync(db, db.StockSizes.Where(s => s.Id == copy.Id), copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteStockSizeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await db.StockSizes.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeedCorrection>> ListCorrectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await db.FeedCorrections.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes learned corrections; null filters match everything.</summary>
    public async Task<int> ResetCorrectionsAsync(Guid? id = null, Guid? materialId = null, Guid? toolId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var query = db.FeedCorrections.AsQueryable();
        if (id is { } i)
        {
            query = query.Where(c => c.Id == i);
        }

        if (materialId is { } m)
        {
            query = query.Where(c => c.MaterialId == m);
        }

        if (toolId is { } t)
        {
            query = query.Where(c => c.ToolId == t);
        }

        return await query.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobFeedback>> ListFeedbackAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var feedback = await db.JobFeedback.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. feedback.OrderByDescending(f => f.RecordedAt)];
    }

    public async Task<IReadOnlyList<ToolWearLog>> ListWearAsync(Guid? toolId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var query = db.ToolWearLogs.AsNoTracking();
        if (toolId is { } id)
        {
            query = query.Where(w => w.ToolId == id);
        }

        var logs = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. logs.OrderByDescending(w => w.LoggedAt)];
    }

    /// <summary>Manual wear entry (e.g. tool replaced or found worn) from the tool library.</summary>
    public async Task<ToolWearLog> LogWearAsync(Guid toolId, WearEventKind kind, string? notes, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var log = new ToolWearLog { ToolId = toolId, Kind = kind, LoggedAt = time.GetUtcNow(), Notes = notes };
        db.ToolWearLogs.Add(log);
        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == toolId, cancellationToken).ConfigureAwait(false);
        if (tool is not null)
        {
            tool.Status = kind switch
            {
                WearEventKind.Wear => ToolStatus.Worn,
                WearEventKind.Breakage => ToolStatus.Broken,
                WearEventKind.Replaced => ToolStatus.InStock,
                _ => tool.Status,
            };
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return log;
    }

    /// <summary>Runtime and cut length per tool since its last replacement.</summary>
    public async Task<IReadOnlyDictionary<Guid, ToolUsage>> ToolUsageAsync(CancellationToken cancellationToken = default)
    {
        var logs = await ListWearAsync(null, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, ToolUsage>();
        foreach (var group in logs.GroupBy(l => l.ToolId))
        {
            var ordered = group.OrderBy(l => l.LoggedAt).ToList();
            var replaced = ordered.FindLastIndex(l => l.Kind == WearEventKind.Replaced);
            var since = ordered.Skip(replaced + 1).ToList();
            result[group.Key] = new ToolUsage(
                group.Key,
                since.Sum(l => l.RuntimeMinutes),
                since.Sum(l => l.CutLength),
                since.Count(l => l.JobFeedbackId is not null && l.Kind == WearEventKind.Usage),
                replaced >= 0 ? ordered[replaced].LoggedAt : null,
                ordered.LastOrDefault(l => l.Kind != WearEventKind.Usage)?.Kind);
        }

        return result;
    }

    private Task<LibraryDbContext> OpenAsync(CancellationToken cancellationToken) => LibraryDatabase.OpenAsync(LibraryPath, cancellationToken);

    private static async Task ReplaceAsync<T>(LibraryDbContext db, IQueryable<T> existing, T entity, CancellationToken cancellationToken)
        where T : class
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await existing.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(bool condition, string message)
    {
        if (!condition)
        {
            throw new LibraryValidationException(message);
        }
    }
}

/// <summary>A library entry is incomplete or inconsistent; the message is shown to the user.</summary>
public sealed class LibraryValidationException : Exception
{
    public LibraryValidationException()
    {
    }

    public LibraryValidationException(string message)
        : base(message)
    {
    }

    public LibraryValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
