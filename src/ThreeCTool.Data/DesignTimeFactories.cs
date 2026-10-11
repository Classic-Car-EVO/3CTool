using Microsoft.EntityFrameworkCore.Design;

namespace ThreeCTool.Data;

// Used only by `dotnet ef migrations add`.
internal sealed class LibraryDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args) => new(LibraryDatabase.CreateOptions("design-time-library.db"));
}

internal sealed class ProjectDbContextFactory : IDesignTimeDbContextFactory<ProjectDbContext>
{
    public ProjectDbContext CreateDbContext(string[] args) => new(ProjectDatabase.CreateOptions("design-time-project.db"));
}
