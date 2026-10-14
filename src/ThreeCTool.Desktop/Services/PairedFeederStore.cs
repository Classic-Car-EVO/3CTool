using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Data;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Desktop.Services;

/// <summary>
/// Feeders this desktop is paired with, stored in the library database. Tokens are encrypted at rest with
/// <see cref="SecretProtector"/>; returned entities carry the decrypted token.
/// </summary>
public sealed class PairedFeederStore(string libraryPath, TimeProvider? time = null, SecretProtector? protector = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly SecretProtector protector = protector ?? SecretProtector.ForLibrary(libraryPath);

    public string LibraryPath { get; } = libraryPath;

    /// <summary>Lists the feeders; tokens still stored in plain text (older versions) are encrypted on the way.</summary>
    public async Task<IReadOnlyList<PairedFeeder>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var feeders = await db.PairedFeeders.ToListAsync(cancellationToken).ConfigureAwait(false);
        var legacy = feeders.Where(f => f.Token.Length > 0 && !SecretProtector.IsProtected(f.Token)).ToList();
        foreach (var feeder in legacy)
        {
            feeder.Token = protector.Protect(feeder.Token);
        }

        if (legacy.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return [.. feeders.Select(Decrypted).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<PairedFeeder?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var feeder = await db.PairedFeeders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);
        return feeder is null ? null : Decrypted(feeder);
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
        feeder.Token = protector.Protect(pairing.Token);
        feeder.MachineProfileId = machineProfileId ?? feeder.MachineProfileId;
        feeder.PairedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Decrypted(feeder);
    }

    /// <summary>Links a feeder to the machine profile it drives (used to import its controller configuration).</summary>
    public async Task SetMachineAsync(Guid id, Guid? machineProfileId, CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        await db.PairedFeeders.Where(f => f.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.MachineProfileId, machineProfileId), cancellationToken).ConfigureAwait(false);
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

    private PairedFeeder Decrypted(PairedFeeder stored) => new()
    {
        Id = stored.Id,
        FeederId = stored.FeederId,
        Name = stored.Name,
        Address = stored.Address,
        CertificateFingerprint = stored.CertificateFingerprint,
        PairingId = stored.PairingId,
        Token = protector.Unprotect(stored.Token),
        MachineProfileId = stored.MachineProfileId,
        PairedAt = stored.PairedAt,
        LastSyncAt = stored.LastSyncAt,
    };
}
