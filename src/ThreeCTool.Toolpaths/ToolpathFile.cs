using System.IO.Compression;
using System.Text;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths;

/// <summary>Cached toolpath file: gzip-compressed binary, magic "3CTP", version 1, little-endian.</summary>
public static class ToolpathFile
{
    private const uint Magic = 0x50544333; // "3CTP"
    private const int Version = 1;

    public static void Save(Toolpath toolpath, string path)
    {
        using var file = File.Create(path);
        Write(toolpath, file);
    }

    public static Toolpath Load(string path)
    {
        using var file = File.OpenRead(path);
        return Read(file);
    }

    public static void Write(Toolpath toolpath, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(toolpath.OperationId.ToByteArray());
        writer.Write((int)toolpath.Strategy);
        writer.Write(toolpath.ToolId.ToByteArray());
        var frame = toolpath.Frame;
        writer.Write((int)frame.Orientation);
        writer.Write(frame.OriginX);
        writer.Write(frame.OriginY);
        writer.Write(frame.OriginZ);
        writer.Write(frame.IndexAngle);
        writer.Write(toolpath.Clearance);
        writer.Write(toolpath.Warnings.Count);
        foreach (var warning in toolpath.Warnings)
        {
            writer.Write(warning);
        }

        writer.Write(toolpath.Moves.Count);
        foreach (var move in toolpath.Moves)
        {
            writer.Write((byte)move.Kind);
            writer.Write(move.X);
            writer.Write(move.Y);
            writer.Write(move.Z);
            writer.Write(move.A);
        }
    }

    public static Toolpath Read(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a 3C Tool toolpath file.");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported toolpath file version {version}.");
        }

        var operationId = new Guid(reader.ReadBytes(16));
        var strategy = (StrategyType)reader.ReadInt32();
        var toolId = new Guid(reader.ReadBytes(16));
        var frame = new SetupFrame((SetupOrientation)reader.ReadInt32(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        var clearance = reader.ReadDouble();
        var warnings = new List<string>();
        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            warnings.Add(reader.ReadString());
        }

        var count = reader.ReadInt32();
        var moves = new List<ToolpathMove>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = (MoveKind)reader.ReadByte();
            moves.Add(new ToolpathMove(kind, reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        }

        return new Toolpath
        {
            OperationId = operationId,
            Strategy = strategy,
            ToolId = toolId,
            Frame = frame,
            Clearance = clearance,
            Warnings = warnings,
            Moves = moves,
        };
    }
}
