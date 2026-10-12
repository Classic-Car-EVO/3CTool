using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Data;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Desktop.Services;

/// <summary>Feeders this desktop is paired with, stored in the library database.</summary>
public sealed class PairedFeederStore(string libraryPath, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public string LibraryPath { get; } = libraryPath;

    public async Task<IReadOnlyList<PairedFeeder>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var feeders = await db.PairedFeeders.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. feeders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<PairedFeeder?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        return await db.PairedFeeders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a new pairing; pairing the same feeder again replaces its address, certificate pin and token.</summary>
    public async Task<PairedFeeder> SaveAsync(PairingResult pairing, Guid? machineProfileId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var feeder = await db.PairedFeeders.FirstOrDefaultAsync(f => f.FeederId == pairing.FeederId, cancellationToken).ConfigureAwait(false);
        if (feeder is null)
        {
            feeder = new PairedFeeder { FeederId = pairing.FeederId };
            db.PairedFeeders.Add(feeder);
        }

        feeder.Name = pairing.Name;
        feeder.Address = pairing.Address.ToString();
        feeder.CertificateFingerprint = pairing.Fingerprint;
        feeder.PairingId = pairing.PairingId;
        feeder.Token = pairing.Token;
        feeder.MachineProfileId = machineProfileId ?? feeder.MachineProfileId;
        feeder.PairedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return feeder;
    }

    /// <summary>Forgets a feeder locally (revoke it on the feeder too, from its pairing list).</summary>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        await db.PairedFeeders.Where(f => f.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task MarkSyncedAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        await db.PairedFeeders.Where(f => f.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastSyncAt, now), cancellationToken).ConfigureAwait(false);
    }
}
