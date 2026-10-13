using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Desktop.Tests;

public sealed class LibraryTests : IAsyncDisposable
{
    private readonly DesktopFixture fixture = new();

    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public void SecretProtector_RoundTripsAndRejectsTampering()
    {
        var protector = SecretProtector.ForLibrary(fixture.LibraryPath);
        var stored = protector.Protect("token-123");

        Assert.True(SecretProtector.IsProtected(stored));
        Assert.DoesNotContain("token-123", stored, StringComparison.Ordinal);
        Assert.Equal("token-123", protector.Unprotect(stored));
        Assert.Equal("legacy", protector.Unprotect("legacy"));
        Assert.Equal("token-123", SecretProtector.ForLibrary(fixture.LibraryPath).Unprotect(stored));

        var prefix = stored[..(stored.IndexOf(':', StringComparison.Ordinal) + 1)];
        var bytes = Convert.FromBase64String(stored[prefix.Length..]);
        bytes[^1] ^= 0x01;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(prefix + Convert.ToBase64String(bytes)));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(protector.KeyPath));
        }
    }

    [Fact]
    public async Task PairedFeederStore_EncryptsTokensAndMigratesPlainText()
    {
        await using (var db = await LibraryDatabase.OpenAsync(fixture.LibraryPath))
        {
            db.PairedFeeders.Add(new PairedFeeder { FeederId = "legacy", Name = "Old", Address = "https://old:5443", Token = "plain-token" });
            await db.SaveChangesAsync();
        }

        var store = new PairedFeederStore(fixture.LibraryPath);
        var listed = await store.ListAsync();
        Assert.Equal("plain-token", Assert.Single(listed).Token);
        var saved = await store.SaveAsync(new PairingResult("new", "New", new Uri("https://new:5443"), "AB", "p1", "secret-token"));
        Assert.Equal("secret-token", saved.Token);
        Assert.Equal("secret-token", (await store.GetAsync(saved.Id))!.Token);

        await using (var db = await LibraryDatabase.OpenAsync(fixture.LibraryPath))
        {
            Assert.All(db.PairedFeeders, f => Assert.True(SecretProtector.IsProtected(f.Token)));
        }

        var machine = await fixture.DefaultMachineIdAsync();
        await store.SetMachineAsync(saved.Id, machine);
        Assert.Equal(machine, (await store.GetAsync(saved.Id))!.MachineProfileId);
    }

    [Fact]
    public async Task Machines_DuplicateSaveAndKeepAtLeastOne()
    {
        var library = fixture.Library;
        var original = Assert.Single(await library.ListMachinesAsync());
        Assert.NotEmpty(original.Collets);

        var copy = LibraryService.Duplicate(original, "Copy");
        await library.SaveMachineAsync(copy);
        var machines = await library.ListMachinesAsync();
        Assert.Equal(2, machines.Count);
        var loaded = machines.Single(m => m.Id == copy.Id);
        Assert.Equal(original.Collets.Count, loaded.Collets.Count);
        Assert.DoesNotContain(loaded.Collets, c => original.Collets.Any(o => o.Id == c.Id));

        loaded.Collets.RemoveAt(0);
        loaded.SafeZMachine = -7;
        await library.SaveMachineAsync(loaded);
        var reloaded = (await library.GetMachineAsync(copy.Id))!;
        Assert.Equal(original.Collets.Count - 1, reloaded.Collets.Count);
        Assert.Equal(-7, reloaded.SafeZMachine);

        await library.DeleteMachineAsync(copy.Id);
        await Assert.ThrowsAsync<LibraryValidationException>(() => library.DeleteMachineAsync(original.Id));
    }

    [Fact]
    public async Task ToolsAndMaterials_AreValidatedAndBuiltInsProtected()
    {
        var library = fixture.Library;
        var tool = DesktopFixture.FlatEndMill();
        await library.SaveToolAsync(tool);
        tool.Stickout = 30;
        await library.SaveToolAsync(tool);
        Assert.Equal(30, Assert.Single(await library.ListToolsAsync()).Stickout);
        await Assert.ThrowsAsync<LibraryValidationException>(() => library.SaveToolAsync(new Tool { Name = "Bad", Diameter = 0, Stickout = 10 }));

        var builtIn = (await library.ListMaterialsAsync()).First(m => m.IsBuiltIn);
        await Assert.ThrowsAsync<LibraryValidationException>(() => library.DeleteMaterialAsync(builtIn.Id));

        await library.LogWearAsync(tool.Id, Core.Learning.WearEventKind.Breakage, "snapped");
        Assert.Equal(ToolStatus.Broken, Assert.Single(await library.ListToolsAsync()).Status);
        Assert.Single(await library.ListWearAsync(tool.Id));
    }

    [Fact]
    public async Task DesktopSettings_RoundTripAndRecentList()
    {
        var store = new DesktopSettingsStore(fixture.LibraryPath);
        var settings = await store.LoadAsync();
        Assert.Equal(Core.LengthUnit.Millimeter, settings.DisplayUnit);

        for (var i = 0; i < 12; i++)
        {
            DesktopSettingsStore.AddRecent(settings, Path.Combine(fixture.Root, $"p{i}"));
        }

        DesktopSettingsStore.AddRecent(settings, Path.Combine(fixture.Root, "p5"));
        settings.DisplayUnit = Core.LengthUnit.Inch;
        settings.ComputeBackend = Compute.ComputeBackend.Cpu;
        await store.SaveAsync(settings);

        var loaded = await store.LoadAsync();
        Assert.Equal(Core.LengthUnit.Inch, loaded.DisplayUnit);
        Assert.Equal(Compute.ComputeBackend.Cpu, loaded.ComputeBackend);
        Assert.Equal(DesktopSettings.MaxRecentProjects, loaded.RecentProjects.Count);
        Assert.EndsWith("p5", loaded.RecentProjects[0], StringComparison.Ordinal);
        Assert.Single(loaded.RecentProjects, p => p.EndsWith("p5", StringComparison.Ordinal));
    }

    [Fact]
    public void GcodeSections_SplitAtOperationComments()
    {
        var lines = GcodeSections.SplitLines(
            "(3C Tool milling program)\n(Operations: Rough 1, Finish 1)\nG21\n(Operation: Rough 1, ZLevelRoughing)\nG1X1\nG1X2\n(Operation: Odd, name, ParallelFinishing)\nG1X3\nM30\n");
        Assert.Equal(9, lines.Count);
        var sections = GcodeSections.Parse(lines);
        Assert.Equal(
            [(4, 6, Core.Projects.StrategyType.ZLevelRoughing), (7, 9, Core.Projects.StrategyType.ParallelFinishing)],
            sections);
        Assert.Equal("4-6:ZLevelRoughing;7-9:ParallelFinishing", FeederTags.FormatSections(sections));
    }
}
