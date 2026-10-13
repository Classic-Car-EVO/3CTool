using Avalonia.Controls;
using Avalonia.Platform.Storage;

using ThreeCTool.Desktop.ViewModels;

namespace ThreeCTool.Desktop.Views;

/// <summary>File dialogs through the window's storage provider (native dialogs or the portal on Linux).</summary>
public sealed class StorageFileDialogs(TopLevel owner) : IFileDialogs
{
    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<FileType> types)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = Filters(types),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileType> types)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = Filters(types),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static List<FilePickerFileType> Filters(IReadOnlyList<FileType> types) =>
        [.. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns })];
}
