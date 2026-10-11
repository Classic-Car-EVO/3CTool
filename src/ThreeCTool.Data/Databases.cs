using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ThreeCTool.Data;

public static class LibraryDatabase
{
    private const string SeedVersionKey = "BuiltInLibraryVersion";

    /// <summary><c>~/.local/share/3CTool/library.db</c> on Linux, <c>%LOCALAPPDATA%\3CTool\library.db</c> on Windows.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "3CTool",
        "library.db");

    public static DbContextOptions<LibraryDbContext> CreateOptions(string path) =>
        new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;

    /// <summary>Opens (creating if needed) the library, applies migrations and inserts new built-in content.</summary>
    public static async Task<LibraryDbContext> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new LibraryDbContext(CreateOptions(path));
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
            await SeedAsync(db, cancellationToken);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task SeedAsync(LibraryDbContext db, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.FindAsync([SeedVersionKey], cancellationToken);
        var installed = setting is null ? 0 : int.Parse(setting.Value, CultureInfo.InvariantCulture);
        if (installed >= BuiltInLibrary.Version)
        {
            return;
        }

        await AddMissingAsync(db.MachineProfiles, [BuiltInLibrary.DefaultMachine()], m => m.Id, cancellationToken);
        await AddMissingAsync(db.Materials, BuiltInLibrary.Materials(), m => m.Id, cancellationToken);
        await AddMissingAsync(db.Filaments, BuiltInLibrary.Filaments(), f => f.Id, cancellationToken);
        await AddMissingAsync(db.PrintProfiles, BuiltInLibrary.PrintProfiles(), p => p.Id, cancellationToken);

        var value = BuiltInLibrary.Version.ToString(CultureInfo.InvariantCulture);
        if (setting is null)
        {
            db.Settings.Add(new LibrarySetting { Key = SeedVersionKey, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task AddMissingAsync<T>(DbSet<T> set, IEnumerable<T> items, Func<T, Guid> id, CancellationToken cancellationToken)
        where T : class
    {
        foreach (var item in items)
        {
            if (await set.FindAsync([id(item)], cancellationToken) is null)
            {
                set.Add(item);
            }
        }
    }
}

public static class ProjectDatabase
{
    public const string FileName = "project.db";

    public static DbContextOptions<ProjectDbContext> CreateOptions(string path) =>
        new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;

    /// <summary>Opens (creating if needed) <c>project.db</c> in <paramref name="projectFolder"/> and applies migrations.</summary>
    public static async Task<ProjectDbContext> OpenAsync(string projectFolder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(projectFolder);
        var db = new ProjectDbContext(CreateOptions(Path.Combine(projectFolder, FileName)));
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
