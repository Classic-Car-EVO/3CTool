using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Data;

namespace ThreeCTool.Post;

/// <summary>Writes posted files into the project folder and records them in <c>project.db</c>.</summary>
public static class GcodeFileStore
{
    /// <summary>
    /// Writes <paramref name="output"/> atomically (temp file + rename) under <paramref name="projectFolder"/> and
    /// replaces any <see cref="GcodeFile"/> row with the same path. Returns the full path.
    /// </summary>
    public static async Task<string> SaveAsync(
        ProjectDbContext db,
        string projectFolder,
        PostOutput output,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(output);
        var root = Path.GetFullPath(projectFolder);
        var path = Path.GetFullPath(Path.Combine(root, output.FileName));
        if (Path.IsPathRooted(output.FileName) ||
            !path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PostException($"'{output.FileName}' is outside the project folder.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, output.Text, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);

        var relative = output.FileName.Replace('\\', '/');
        var existing = await db.GcodeFiles.Where(g => g.Path == relative).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.GcodeFiles.RemoveRange(existing);
        db.GcodeFiles.Add(new GcodeFile
        {
            SetupId = output.SetupId,
            Piece = output.Piece,
            Head = ToolheadType.Spindle,
            ToolId = output.ToolId,
            Path = relative,
            Sha256 = output.Sha256,
            LineCount = output.LineCount,
            Rpm = output.Rpm,
            EstimatedSeconds = output.EstimatedSeconds,
            GeneratedAt = generatedAt,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }
}
