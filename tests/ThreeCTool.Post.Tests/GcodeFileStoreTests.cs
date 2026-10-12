using ThreeCTool.Data;

namespace ThreeCTool.Post.Tests;

public sealed class GcodeFileStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "3ctool-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAsync_WritesTheFileAndReplacesItsRecord()
    {
        var post = new FluidNcMillingPost();
        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves()));
        var when = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await using (var db = await ProjectDatabase.OpenAsync(folder))
        {
            var path = await GcodeFileStore.SaveAsync(db, folder, output, when);
            Assert.Equal(Path.Combine(folder, "gcode", "01_Top_T01_Flat_6.nc"), path);
            Assert.Equal(output.Text, await File.ReadAllTextAsync(path));
            Assert.Equal(output.Text.Length, new FileInfo(path).Length);
            await GcodeFileStore.SaveAsync(db, folder, output, when.AddHours(1));
        }

        await using (var db = await ProjectDatabase.OpenAsync(folder))
        {
            var record = Assert.Single(db.GcodeFiles);
            Assert.Equal("gcode/01_Top_T01_Flat_6.nc", record.Path);
            Assert.Equal(output.Sha256, record.Sha256);
            Assert.Equal(output.LineCount, record.LineCount);
            Assert.Equal(output.SetupId, record.SetupId);
            Assert.Equal(output.ToolId, record.ToolId);
            Assert.Equal(18000, record.Rpm);
            Assert.Equal(when.AddHours(1), record.GeneratedAt);
        }

        Assert.False(File.Exists(Path.Combine(folder, "gcode", "01_Top_T01_Flat_6.nc.tmp")));
    }

    [Theory]
    [InlineData("../outside.nc")]
    [InlineData("gcode/../../outside.nc")]
    [InlineData("/tmp/outside.nc")]
    public async Task SaveAsync_PathOutsideTheProject_Throws(string fileName)
    {
        var output = new PostOutput { FileName = fileName, Lines = ["M30"], Text = "M30\n", Sha256 = string.Empty };
        await using var db = await ProjectDatabase.OpenAsync(folder);
        await Assert.ThrowsAsync<PostException>(() => GcodeFileStore.SaveAsync(db, folder, output, DateTimeOffset.UnixEpoch));
        Assert.Empty(db.GcodeFiles);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
