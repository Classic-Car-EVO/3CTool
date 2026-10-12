using System.Numerics;
using System.Runtime.InteropServices;

using ThreeCTool.Compute;

namespace ThreeCTool.Simulation;

/// <summary>
/// Playback of the in-process stock: <see cref="Seek"/> moves the stock to the state after a given move. Going
/// backwards replays from the nearest stored step start (the initial stock is always kept).
/// </summary>
public sealed class SimulationSession : IDisposable
{
    private readonly List<PreparedStep> _steps;
    private readonly Dictionary<int, DexelStock> _checkpoints = [];
    private readonly int _maxCheckpoints;

    private SimulationSession(List<PreparedStep> steps, DexelStock stock, float resolution, int maxCheckpoints)
    {
        _steps = steps;
        Stock = stock;
        Resolution = resolution;
        _maxCheckpoints = Math.Max(1, maxCheckpoints);
        _checkpoints[0] = stock.Clone();
    }

    /// <summary>Current in-process stock (part frame). Owned by the session.</summary>
    public DexelStock Stock { get; }

    public float Resolution { get; }

    /// <summary>Step being shown; equal to the step count after the last step.</summary>
    public int Step { get; private set; }

    /// <summary>Last executed move of <see cref="Step"/> (0 = the step's start position).</summary>
    public int Move { get; private set; }

    public int StepCount => _steps.Count;

    public int MoveCount(int step) => _steps[step].MoveCount;

    /// <param name="maxCheckpoints">Stock copies kept at step starts (including the initial stock) to speed up seeking back.</param>
    public static SimulationSession Create(SimulationJob job, int maxCheckpoints = 4)
    {
        ArgumentNullException.ThrowIfNull(job);
        var steps = job.Steps.Select(s => PreparedStep.Create(s, job.Options.MaxAngleStep)).ToList();
        var stock = StockBuilder.Create(job, steps, out var resolution);
        try
        {
            return new SimulationSession(steps, stock, resolution, maxCheckpoints);
        }
        catch
        {
            stock.Dispose();
            throw;
        }
    }

    /// <summary>Tool tip and axis (part frame) at the target of a move.</summary>
    public (Vector3 Tip, Vector3 Axis) ToolAt(int step, int move)
    {
        var segments = _steps[step].Segments;
        if (segments.Count == 0)
        {
            return (Vector3.Zero, Vector3.UnitZ);
        }

        var index = segments.FindLastIndex(s => s.Source <= move);
        return index < 0 ? (segments[0].Move.From, segments[0].Move.Axis) : (segments[index].Move.To, segments[index].Move.Axis);
    }

    /// <summary>Stock state after move <paramref name="move"/> of step <paramref name="step"/>, all earlier steps complete.</summary>
    public void Seek(int step, int move)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(step, _steps.Count);
        if (step == _steps.Count)
        {
            move = 0;
        }
        else
        {
            move = Math.Clamp(move, 0, Math.Max(_steps[step].MoveCount - 1, 0));
        }

        if (step < Step || (step == Step && move < Move))
        {
            var restore = _checkpoints.Keys.Where(k => k <= step).Max();
            Stock.CopyFrom(_checkpoints[restore]);
            (Step, Move) = (restore, 0);
        }

        while (Step < step)
        {
            Advance(Step, Move, int.MaxValue);
            (Step, Move) = (Step + 1, 0);
            if (Step < _steps.Count && !_checkpoints.ContainsKey(Step) && _checkpoints.Count < _maxCheckpoints)
            {
                _checkpoints[Step] = Stock.Clone();
            }
        }

        if (Step < _steps.Count && move > Move)
        {
            Advance(Step, Move, move);
            Move = move;
        }
    }

    public void Dispose()
    {
        Stock.Dispose();
        foreach (var checkpoint in _checkpoints.Values)
        {
            checkpoint.Dispose();
        }
    }

    /// <summary>Applies the moves (from, to] of a step.</summary>
    private void Advance(int step, int from, int to)
    {
        var p = _steps[step];
        var segments = p.Segments;
        var first = segments.FindIndex(s => s.Source > from);
        if (first < 0)
        {
            return;
        }

        var end = segments.FindIndex(first, s => s.Source > to);
        p.Apply(Stock, CollectionsMarshal.AsSpan(segments)[first..(end < 0 ? segments.Count : end)]);
    }
}
