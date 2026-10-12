namespace ThreeCTool.Feeds;

/// <summary>
/// Slows individual moves whose measured engagement (removed cross-section, mm²) exceeds the nominal one: the
/// heavier cut is re-evaluated at the same RPM (wider up to a full slot, then deeper) and the feed scaled to match.
/// </summary>
public sealed class EngagementLimiter
{
    private readonly FeedsEngine _engine;
    private readonly FeedRequest _request;
    private readonly FeedEvaluation _nominal;
    private readonly FeedsOptions _options;

    internal EngagementLimiter(FeedsEngine engine, FeedRequest request, FeedEvaluation nominal)
    {
        _engine = engine;
        _request = request;
        _nominal = nominal;
        _options = engine.Context.Options;
    }

    public double NominalArea => _nominal.Engagement.Area;

    /// <summary>Feed factor (≤ 1, in steps of <see cref="FeedsOptions.FeedFactorStep"/>) for a removed cross-section.</summary>
    public double Factor(double area)
    {
        if (!(area > NominalArea * _options.EngagementMargin) || _nominal.FeedRate <= 0)
        {
            return 1;
        }

        var d = _request.Tool.Diameter;
        var ap = _nominal.Engagement.AxialDepth;
        var ae = Math.Min(area / ap, d);
        var heavy = _engine.Evaluate(_request, _nominal.Rpm, new Engagement(area / ae, ae));
        var factor = Math.Min(1, heavy.FeedRate / _nominal.FeedRate);
        var step = _options.FeedFactorStep;
        factor = step > 0 ? Math.Floor((factor / step) + 1e-9) * step : factor;
        return Math.Clamp(factor, _options.MinFeedFactor, 1);
    }
}
