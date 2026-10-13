using ThreeCTool.Data;

namespace ThreeCTool.Desktop.Services;

/// <summary>Per-user data locations (library, direct-connection feeder data, default projects folder).</summary>
public sealed record AppPaths(string DataDirectory)
{
    public static AppPaths Default { get; } = new(Path.GetDirectoryName(LibraryDatabase.DefaultPath)!);

    public string LibraryPath => Path.Combine(DataDirectory, "library.db");

    /// <summary>Data directory of the embedded feeder used for a direct controller connection.</summary>
    public string DirectFeederDirectory => Path.Combine(DataDirectory, "direct");

    public static string DefaultProjectsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create),
        "3C Tool Projects");
}
