using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using ThreeCTool.Core.Machines;

namespace ThreeCTool.Streaming.Config;

/// <summary>A node of the indentation-structured FluidNC configuration (<c>key: value</c> YAML subset).</summary>
public sealed class ConfigNode(string name, string? value)
{
    public string Name { get; } = name;

    public string? Value { get; } = value;

    public List<ConfigNode> Children { get; } = [];

    public ConfigNode? this[string name] => Children.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Looks up a slash-separated path such as <c>axes/x/steps_per_mm</c>.</summary>
    public ConfigNode? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var node = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node[part];
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    public IEnumerable<ConfigNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));

    public string? GetString(string path) => Find(path)?.Value;

    public double? GetDouble(string path) =>
        double.TryParse(GetString(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool? GetBool(string path) => GetString(path)?.ToUpperInvariant() switch
    {
        "TRUE" or "YES" or "1" => true,
        "FALSE" or "NO" or "0" => false,
        _ => null,
    };
}

/// <summary>The running FluidNC configuration as returned by <c>$Config/Dump</c>.</summary>
public sealed class ControllerConfig
{
    private ControllerConfig(ConfigNode root, string hash, string text)
    {
        Root = root;
        Hash = hash;
        Text = text;
    }

    public ConfigNode Root { get; }

    /// <summary>SHA-256 (hex) of the normalised dump, stored in the machine profile at import.</summary>
    public string Hash { get; }

    public string Text { get; }

    public string? Name => Root.GetString("name");

    /// <summary>Controller reports the executing <c>N</c> line number (<c>Ln:</c>) in status reports.</summary>
    public bool UseLineNumbers => Root.GetBool("use_line_numbers") ?? false;

    public int PlannerBlocks => (int)(Root.GetDouble("planner_blocks") ?? 16);

    public double JunctionDeviation => Root.GetDouble("junction_deviation_mm") ?? 0.01;

    public bool MustHome => Root.GetBool("start/must_home") ?? true;

    public static ControllerConfig Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var root = new ConfigNode(string.Empty, null);
        var stack = new List<(int Indent, ConfigNode Node)> { (-1, root) };
        var normalised = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var content = line.TrimStart();
            if (content.Length == 0 || content.StartsWith('[') || content.StartsWith('#') || content == "ok")
            {
                continue;
            }

            var colon = content.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            normalised.Add(line);
            var indent = line.Length - content.Length;
            var value = content[(colon + 1)..].Trim();
            var node = new ConfigNode(content[..colon].Trim(), value.Length == 0 ? null : Unquote(value));
            while (stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
        }

        var text = string.Join('\n', normalised);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new ControllerConfig(root, hash, text);
    }

    /// <summary>Axis settings owned by the controller, in profile form.</summary>
    public IReadOnlyList<AxisConfig> Axes()
    {
        var result = new List<AxisConfig>();
        if (Root["axes"] is not { } axes)
        {
            return result;
        }

        foreach (var node in axes.Children)
        {
            if (node.Name.Length != 1 || !Enum.TryParse<AxisName>(node.Name, ignoreCase: true, out var axis))
            {
                continue;
            }

            var motors = node.Children.Where(c => c.Name.StartsWith("motor", StringComparison.OrdinalIgnoreCase)
                && c["null_motor"] is null).ToList();
            var direction = motors.FirstOrDefault()?.Descendants().FirstOrDefault(d => d.Name == "direction_pin")?.Value;
            result.Add(new AxisConfig
            {
                Axis = axis,
                StepsPerUnit = node.GetDouble("steps_per_mm") ?? 0,
                MaxRatePerMinute = node.GetDouble("max_rate_mm_per_min") ?? 0,
                AccelerationPerSecond2 = node.GetDouble("acceleration_mm_per_sec2") ?? 0,
                MaxTravel = node.GetDouble("max_travel_mm") ?? 0,
                SoftLimits = node.GetBool("soft_limits") ?? false,
                DirectionInverted = direction?.Contains(":low", StringComparison.OrdinalIgnoreCase) ?? false,
                MotorCount = Math.Max(motors.Count, 1),
                HomingCycle = (int)(node.GetDouble("homing/cycle") ?? 0),
                HomingPositiveDirection = node.GetBool("homing/positive_direction") ?? false,
                HomingMachinePosition = node.GetDouble("homing/mpos_mm") ?? 0,
            });
        }

        return result;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value;
}
