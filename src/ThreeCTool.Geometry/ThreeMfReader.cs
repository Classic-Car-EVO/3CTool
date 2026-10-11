using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace ThreeCTool.Geometry;

/// <summary>
/// Reads 3MF (core spec + production-extension external model parts). Every mesh object instance
/// placed on the build plate (directly or via components) becomes one named body, converted to millimetres.
/// </summary>
public static class ThreeMfReader
{
    private const string CoreNamespace = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string ProductionNamespace = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ModelRelationshipType = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
    private const int MaxComponentDepth = 32;

    public static IReadOnlyList<Mesh> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static IReadOnlyList<Mesh> Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var context = new ReadContext(archive);
        var rootPath = FindRootModelPath(archive);
        var root = context.Load(rootPath);

        var bodies = new List<Mesh>();
        foreach (var item in root.BuildItems)
        {
            var file = item.Path is null ? root : context.Load(item.Path);
            var unitFix = Matrix4x4.CreateScale(file.MillimetersPerUnit / root.MillimetersPerUnit);
            var transform = unitFix * item.Transform * Matrix4x4.CreateScale(root.MillimetersPerUnit);
            context.Collect(file, item.ObjectId, transform, item.Path ?? rootPath, null, bodies, depth: 0);
        }

        if (bodies.Count == 0)
        {
            throw new InvalidDataException("3MF file contains no printable mesh objects.");
        }

        return bodies;
    }

    private static string FindRootModelPath(ZipArchive archive)
    {
        var rels = archive.GetEntry("_rels/.rels");
        if (rels is not null)
        {
            using var reader = CreateXmlReader(rels.Open());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.NamespaceURI == RelationshipsNamespace &&
                    reader.GetAttribute("Type") == ModelRelationshipType && reader.GetAttribute("Target") is { } target)
                {
                    return NormalizePath(target);
                }
            }
        }

        return "3D/3dmodel.model";
    }

    private static string NormalizePath(string path) => path.TrimStart('/');

    private static XmlReader CreateXmlReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    });

    private static float ParseFloat(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : throw new InvalidDataException($"Invalid number '{text}' in 3MF model.");

    private static int ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Invalid integer '{text}' in 3MF model.");

    /// <summary>3MF transforms are 3x4, row-vector convention, matching <see cref="Matrix4x4"/>.</summary>
    private static Matrix4x4 ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Matrix4x4.Identity;
        }

        var v = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(ParseFloat).ToArray();
        if (v.Length != 12)
        {
            throw new InvalidDataException("3MF transform must have 12 values.");
        }

        return new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
    }

    private static float MillimetersPer(string? unit) => unit switch
    {
        null or "" or "millimeter" => 1f,
        "micron" => 0.001f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => throw new InvalidDataException($"Unknown 3MF unit '{unit}'."),
    };

    private sealed record Component(int ObjectId, string? Path, Matrix4x4 Transform);

    private sealed class ModelObject
    {
        public string? Name { get; set; }

        public Mesh? Mesh { get; set; }

        public List<Component> Components { get; } = [];
    }

    private sealed class ModelFile
    {
        public float MillimetersPerUnit { get; set; } = 1f;

        public Dictionary<int, ModelObject> Objects { get; } = [];

        public List<Component> BuildItems { get; } = [];
    }

    private sealed class ReadContext(ZipArchive archive)
    {
        private readonly Dictionary<string, ModelFile> _files = new(StringComparer.Ordinal);

        public ModelFile Load(string path)
        {
            if (_files.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"3MF part '{path}' not found.");
            using var reader = CreateXmlReader(entry.Open());
            var file = Parse(reader);
            _files[path] = file;
            return file;
        }

        public void Collect(ModelFile file, int objectId, Matrix4x4 transform, string path, string? parentName, List<Mesh> bodies, int depth)
        {
            if (depth > MaxComponentDepth)
            {
                throw new InvalidDataException("3MF component nesting is too deep (cyclic reference?).");
            }

            if (!file.Objects.TryGetValue(objectId, out var obj))
            {
                throw new InvalidDataException($"3MF object {objectId} not found in '{path}'.");
            }

            var name = obj.Name ?? parentName ?? $"Object {objectId}";
            if (obj.Mesh is not null)
            {
                var body = obj.Mesh.Transform(transform);
                body.Name = name;
                bodies.Add(body);
            }

            foreach (var component in obj.Components)
            {
                var componentPath = component.Path ?? path;
                var componentFile = component.Path is null ? file : Load(componentPath);

                // Units are declared per model part; component geometry uses its own part's unit.
                var unitFix = Matrix4x4.CreateScale(componentFile.MillimetersPerUnit / file.MillimetersPerUnit);
                Collect(componentFile, component.ObjectId, unitFix * component.Transform * transform, componentPath, name, bodies, depth + 1);
            }
        }

        private static ModelFile Parse(XmlReader reader)
        {
            var file = new ModelFile();
            ModelObject? current = null;
            var currentId = 0;
            var vertices = new List<Vector3>();
            var indices = new List<int>();

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "object" && reader.NamespaceURI == CoreNamespace && current is not null)
                {
                    if (indices.Count > 0)
                    {
                        try
                        {
                            current.Mesh = new Mesh([.. vertices], [.. indices], current.Name ?? string.Empty);
                        }
                        catch (ArgumentException ex)
                        {
                            throw new InvalidDataException($"3MF object {currentId}: {ex.Message}", ex);
                        }
                    }

                    file.Objects[currentId] = current;
                    current = null;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != CoreNamespace)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "model":
                        file.MillimetersPerUnit = MillimetersPer(reader.GetAttribute("unit"));
                        break;
                    case "object":
                        var type = reader.GetAttribute("type");
                        currentId = ParseInt(reader.GetAttribute("id"));
                        current = type is null or "model" or "solidsupport" ? new ModelObject { Name = reader.GetAttribute("name") } : null;
                        vertices.Clear();
                        indices.Clear();
                        if (current is not null && reader.IsEmptyElement)
                        {
                            file.Objects[currentId] = current;
                            current = null;
                        }

                        break;
                    case "vertex" when current is not null:
                        vertices.Add(new Vector3(ParseFloat(reader.GetAttribute("x")), ParseFloat(reader.GetAttribute("y")), ParseFloat(reader.GetAttribute("z"))));
                        break;
                    case "triangle" when current is not null:
                        indices.Add(ParseInt(reader.GetAttribute("v1")));
                        indices.Add(ParseInt(reader.GetAttribute("v2")));
                        indices.Add(ParseInt(reader.GetAttribute("v3")));
                        break;
                    case "component" when current is not null:
                        current.Components.Add(new Component(
                            ParseInt(reader.GetAttribute("objectid")),
                            reader.GetAttribute("path", ProductionNamespace) is { } p ? NormalizePath(p) : null,
                            ParseTransform(reader.GetAttribute("transform"))));
                        break;
                    case "item":
                        file.BuildItems.Add(new Component(
                            ParseInt(reader.GetAttribute("objectid")),
                            reader.GetAttribute("path", ProductionNamespace) is { } itemPath ? NormalizePath(itemPath) : null,
                            ParseTransform(reader.GetAttribute("transform"))));
                        break;
                }
            }

            return file;
        }
    }
}
