using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ThreeCTool.Geometry;

/// <summary>
/// Reads binary and ASCII STL. Coordinates are returned as stored (STL has no unit);
/// ASCII files with several <c>solid</c> blocks produce one mesh per solid, named after it.
/// </summary>
public static class StlReader
{
    private const int HeaderSize = 80;
    private const int BinaryTriangleSize = 50;

    public static IReadOnlyList<Mesh> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetFileNameWithoutExtension(path));
    }

    public static IReadOnlyList<Mesh> Read(Stream stream, string defaultName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }

        var start = stream.Position;
        var length = stream.Length - start;
        Span<byte> head = stackalloc byte[HeaderSize + 4];
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;

        var binaryCount = headLength == head.Length ? BinaryPrimitives.ReadUInt32LittleEndian(head[HeaderSize..]) : 0u;
        var binaryLength = HeaderSize + 4 + ((long)binaryCount * BinaryTriangleSize);
        var looksAscii = Encoding.ASCII.GetString(head[..headLength]).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);

        // Many CAD exporters write "solid" into binary headers, so an exact size match wins.
        if (headLength == head.Length && length == binaryLength)
        {
            return [ReadBinary(stream, binaryCount, defaultName)];
        }

        var binaryPossible = headLength == head.Length && length >= binaryLength;
        if (looksAscii)
        {
            try
            {
                return ReadAscii(stream, defaultName);
            }
            catch (InvalidDataException) when (binaryPossible)
            {
                stream.Position = start;
            }
        }

        if (binaryPossible)
        {
            return [ReadBinary(stream, binaryCount, defaultName)];
        }

        throw new InvalidDataException("Not a valid STL file (binary size mismatch and no ASCII 'solid' header).");
    }

    private static Mesh ReadBinary(Stream stream, uint count, string name)
    {
        stream.Seek(HeaderSize + 4, SeekOrigin.Current);
        var corners = new Vector3[checked((int)(count * 3))];
        var buffer = new byte[BinaryTriangleSize * 4096];
        var triangle = 0;
        while (triangle < count)
        {
            var batch = (int)Math.Min(4096, count - triangle);
            stream.ReadExactly(buffer, 0, batch * BinaryTriangleSize);
            for (var i = 0; i < batch; i++)
            {
                var record = buffer.AsSpan(i * BinaryTriangleSize, BinaryTriangleSize);
                for (var v = 0; v < 3; v++)
                {
                    // Skip the 12-byte facet normal; it is recomputed from the vertices.
                    var offset = 12 + (v * 12);
                    var p = new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(record[offset..]),
                        BinaryPrimitives.ReadSingleLittleEndian(record[(offset + 4)..]),
                        BinaryPrimitives.ReadSingleLittleEndian(record[(offset + 8)..]));
                    corners[((triangle + i) * 3) + v] = Validate(p);
                }
            }

            triangle += batch;
        }

        return MeshWelder.FromTriangleSoup(corners, name);
    }

    private static List<Mesh> ReadAscii(Stream stream, string defaultName)
    {
        var meshes = new List<Mesh>();
        var corners = new List<Vector3>();
        string? solidName = null;
        var facetCorners = 0;
        var lineNumber = 0;

        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var text = line.AsSpan().Trim();
            if (text.IsEmpty)
            {
                continue;
            }

            var keywordEnd = text.IndexOfAny(' ', '\t');
            var keyword = keywordEnd < 0 ? text : text[..keywordEnd];
            var rest = keywordEnd < 0 ? [] : text[(keywordEnd + 1)..].Trim();

            if (keyword.Equals("solid", StringComparison.OrdinalIgnoreCase))
            {
                solidName = rest.IsEmpty ? null : rest.ToString();
                corners.Clear();
            }
            else if (keyword.Equals("endsolid", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                solidName = null;
            }
            else if (keyword.Equals("facet", StringComparison.OrdinalIgnoreCase))
            {
                facetCorners = 0;
            }
            else if (keyword.Equals("vertex", StringComparison.OrdinalIgnoreCase))
            {
                corners.Add(Validate(ParseVector(rest, lineNumber)));
                facetCorners++;
            }
            else if (keyword.Equals("endfacet", StringComparison.OrdinalIgnoreCase) && facetCorners != 3)
            {
                throw new InvalidDataException($"Line {lineNumber}: facet has {facetCorners} vertices, expected 3.");
            }
        }

        // Tolerate files missing the final "endsolid".
        Flush();
        if (meshes.Count == 0)
        {
            throw new InvalidDataException("ASCII STL contains no triangles.");
        }

        return meshes;

        void Flush()
        {
            if (corners.Count > 0)
            {
                var name = solidName ?? (meshes.Count == 0 ? defaultName : $"{defaultName} {meshes.Count + 1}");
                meshes.Add(MeshWelder.FromTriangleSoup(corners, name));
                corners.Clear();
            }
        }
    }

    private static Vector3 ParseVector(ReadOnlySpan<char> text, int lineNumber)
    {
        Span<float> values = stackalloc float[3];
        var count = 0;
        foreach (var range in text.SplitAny(' ', '\t'))
        {
            var part = text[range];
            if (part.IsEmpty)
            {
                continue;
            }

            if (count == 3 || !float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[count]))
            {
                throw new InvalidDataException($"Line {lineNumber}: invalid vertex.");
            }

            count++;
        }

        return count == 3 ? new Vector3(values[0], values[1], values[2]) : throw new InvalidDataException($"Line {lineNumber}: invalid vertex.");
    }

    private static Vector3 Validate(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
            ? p
            : throw new InvalidDataException("STL contains a non-finite coordinate.");
}

public static class StlWriter
{
    /// <summary>Writes a binary STL with recomputed facet normals.</summary>
    public static void Write(Stream stream, Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mesh);

        var header = new byte[80];
        Encoding.ASCII.GetBytes("3C Tool binary STL", header);
        stream.Write(header);

        Span<byte> record = stackalloc byte[50];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)mesh.TriangleCount);
        stream.Write(record[..4]);
        record.Clear();

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            WriteVector(record, 0, mesh.FaceNormal(t));
            WriteVector(record, 12, a);
            WriteVector(record, 24, b);
            WriteVector(record, 36, c);
            stream.Write(record);
        }
    }

    public static void Write(string path, Mesh mesh)
    {
        using var stream = File.Create(path);
        Write(stream, mesh);
    }

    private static void WriteVector(Span<byte> record, int offset, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(record[offset..], v.X);
        BinaryPrimitives.WriteSingleLittleEndian(record[(offset + 4)..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(record[(offset + 8)..], v.Z);
    }
}
