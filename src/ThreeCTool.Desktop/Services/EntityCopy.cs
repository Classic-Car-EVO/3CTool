using System.Text.Json;

namespace ThreeCTool.Desktop.Services;

/// <summary>Deep copies of plain entity graphs (detached from any context, safe to hand to background work).</summary>
public static class EntityCopy
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = false };

    public static T Clone<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
    }

    /// <summary>Canonical JSON used for input hashes.</summary>
    public static byte[] Canonical<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
