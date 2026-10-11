using System.Security.Cryptography;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Geometry;

public sealed record MeshBody(string Name, BodyRole Role, Mesh Mesh);

/// <summary>Result of importing a model file.</summary>
/// <param name="UnitsDeclared">True when the file declares its unit (3MF) and was converted to mm; STL is unitless and assumed mm.</param>
public sealed record ImportedModel(IReadOnlyList<MeshBody> Bodies, bool UnitsDeclared, string Sha256)
{
    public BoundingBox Bounds => Bodies.Aggregate(BoundingBox.Empty, (box, body) => box.Union(body.Mesh.Bounds));
}

public static class MeshImporter
{
    public static IReadOnlyList<string> SupportedExtensions { get; } = [".stl", ".3mf"];

    public static ImportedModel Load(string path)
    {
        var extension = Path.GetExtension(path).ToUpperInvariant();
        var (meshes, declared) = extension switch
        {
            ".STL" => (StlReader.Read(path), false),
            ".3MF" => (ThreeMfReader.Read(path), true),
            _ => throw new NotSupportedException($"Unsupported model format '{extension}'."),
        };

        var bodies = meshes.Select(m => new MeshBody(m.Name, InferRole(m.Name), m)).ToList();
        return new ImportedModel(bodies, declared, ComputeSha256(path));
    }

    /// <summary>Bodies whose name contains "print" are tagged for printing; everything else is milled.</summary>
    public static BodyRole InferRole(string name) =>
        name.Contains("print", StringComparison.OrdinalIgnoreCase) ? BodyRole.Print : BodyRole.Mill;

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
