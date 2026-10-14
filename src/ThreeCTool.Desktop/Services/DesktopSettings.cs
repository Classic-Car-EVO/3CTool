using System.Text.Json;
using System.Text.Json.Serialization;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Data;
using ThreeCTool.Rendering;

namespace ThreeCTool.Desktop.Services;

/// <summary>Desktop preferences, stored as one JSON value in the library's settings table.</summary>
public sealed class DesktopSettings
{
    public const int MaxRecentProjects = 10;

    /// <summary>Preferred compute backend; null = automatic (CUDA, then OpenCL, then CPU).</summary>
    public ComputeBackend? ComputeBackend { get; set; }

    public PreviewRenderer Renderer { get; set; } = PreviewRenderer.Auto;

    /// <summary>Unit lengths are shown in; entry always accepts any unit suffix.</summary>
    public LengthUnit DisplayUnit { get; set; } = LengthUnit.Millimeter;

    public string? ProjectsFolder { get; set; }

    /// <summary>Project folders, most recent first.</summary>
    public List<string> RecentProjects { get; set; } = [];

    public bool ShowRapids { get; set; } = true;

    /// <summary>Machine profile driven by the direct (USB/TCP) connection; null = not linked.</summary>
    public Guid? DirectMachineProfileId { get; set; }

    public DesktopSettings Clone() => JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(this, SettingsJson.Options), SettingsJson.Options)!;
}

public sealed class DesktopSettingsStore(string libraryPath)
{
    public const string Key = "Desktop.Settings";

    public string LibraryPath { get; } = libraryPath;

    public async Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var row = await db.Settings.FindAsync([Key], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new DesktopSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopSettings>(row.Value, SettingsJson.Options) ?? new DesktopSettings();
        }
        catch (JsonException)
        {
            return new DesktopSettings();
        }
    }

    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, SettingsJson.Options);
        await using var db = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(false);
        var row = await db.Settings.FindAsync([Key], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Settings.Add(new LibrarySetting { Key = Key, Value = json });
        }
        else
        {
            row.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves <paramref name="folder"/> to the top of the recent projects and saves.</summary>
    public static void AddRecent(DesktopSettings settings, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var full = Path.GetFullPath(folder);
        settings.RecentProjects.RemoveAll(p => string.Equals(Path.GetFullPath(p), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        settings.RecentProjects.Insert(0, full);
        if (settings.RecentProjects.Count > DesktopSettings.MaxRecentProjects)
        {
            settings.RecentProjects.RemoveRange(DesktopSettings.MaxRecentProjects, settings.RecentProjects.Count - DesktopSettings.MaxRecentProjects);
        }
    }
}

internal static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };
}
