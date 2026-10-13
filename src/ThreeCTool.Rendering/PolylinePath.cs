using System.Numerics;

namespace ThreeCTool.Rendering;

/// <summary>
/// A tool path as connected strips of vertices, each tagged with the G-code line that reaches it.
/// Strip <c>i</c> spans vertices <c>StripStarts[i]</c> up to the next strip's start (or the end).
/// </summary>
public sealed class PolylinePath
{
    public PolylinePath(float[] positions, int[] lines, int[] stripStarts, bool[] stripRapid)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(stripStarts);
        ArgumentNullException.ThrowIfNull(stripRapid);
        if (positions.Length != 3 * lines.Length)
        {
            throw new ArgumentException("Expected three coordinates per vertex.", nameof(positions));
        }

        if (stripStarts.Length != stripRapid.Length)
        {
            throw new ArgumentException("Every strip needs a rapid flag.", nameof(stripRapid));
        }

        for (var i = 0; i < stripStarts.Length; i++)
        {
            if (stripStarts[i] < 0 || stripStarts[i] > lines.Length || (i > 0 && stripStarts[i] < stripStarts[i - 1]))
            {
                throw new ArgumentException("Strip starts must be ascending vertex indices.", nameof(stripStarts));
            }
        }

        Positions = positions;
        Lines = lines;
        StripStarts = stripStarts;
        StripRapid = stripRapid;
        if (lines.Length > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var i = 0; i < lines.Length; i++)
            {
                var p = Vertex(i);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            (Min, Max) = (min, max);
        }
    }

    public static PolylinePath Empty { get; } = new([], [], [], []);

    public float[] Positions { get; }

    public int[] Lines { get; }

    public int[] StripStarts { get; }

    public bool[] StripRapid { get; }

    public int VertexCount => Lines.Length;

    public Vector3 Min { get; }

    public Vector3 Max { get; }

    public Vector3 Vertex(int index) => new(Positions[3 * index], Positions[(3 * index) + 1], Positions[(3 * index) + 2]);

    /// <summary>Vertex range of strip <paramref name="index"/>.</summary>
    public (int Start, int Count) Strip(int index)
    {
        var start = StripStarts[index];
        var end = index + 1 < StripStarts.Length ? StripStarts[index + 1] : Lines.Length;
        return (start, end - start);
    }
}
