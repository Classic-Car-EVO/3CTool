using System.Numerics;

using Microsoft.Data.Sqlite;

using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;
using ThreeCTool.TestSupport;

namespace ThreeCTool.Desktop.Tests;

/// <summary>Temporary data folder with a library (built-in content plus two test tools) and model files.</summary>
public sealed class DesktopFixture : IAsyncDisposable
{
    public DesktopFixture()
    {
        Directory.CreateDirectory(Root);
        Library = new LibraryService(LibraryPath);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "3ctool-desktop-" + Guid.NewGuid().ToString("N"));

    public string LibraryPath => Path.Combine(Root, "library.db");

    public LibraryService Library { get; }

    public static Tool FlatEndMill() => new()
    {
        Name = "6 mm flat",
        Type = ToolType.FlatEnd,
        Diameter = 6,
        ShankDiameter = 6,
        FluteCount = 2,
        FluteLength = 20,
        OverallLength = 50,
        Stickout = 25,
    };

    public static Tool BallEndMill() => new()
    {
        Name = "3 mm ball",
        Type = ToolType.BallEnd,
        Diameter = 3,
        ShankDiameter = 6,
        FluteCount = 2,
        FluteLength = 12,
        OverallLength = 50,
        Stickout = 25,
    };

    public string ProjectFolder(string name = "project") => Path.Combine(Root, name);

    /// <summary>Binary STL of a box from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public string WriteBoxStl(string name, Vector3 min, Vector3 max)
    {
        var path = Path.Combine(Root, name);
        StlWriter.Write(path, TestMeshes.Box(min, max, Path.GetFileNameWithoutExtension(name)));
        return path;
    }

    public async Task<Guid> DefaultMachineIdAsync() => (await Library.ListMachinesAsync()).Single().Id;

    public async Task<Guid> MaterialIdAsync(string key = "material:pom")
    {
        var id = BuiltInLibrary.IdFor(key);
        Assert.Contains(await Library.ListMaterialsAsync(), m => m.Id == id);
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
