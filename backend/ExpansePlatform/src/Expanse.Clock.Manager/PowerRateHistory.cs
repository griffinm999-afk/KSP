namespace Expanse.Clock.Manager;

// The history contains only live tracked readings. A broken observation or a
// change in contributing facilities starts a new segment.
internal sealed class PowerRateHistory
{
    internal sealed record Point(double Ut, double GenerationEcPerSecond, double ConsumptionEcPerSecond,
        double NetEcPerSecond, string CoverageKey, bool StartsSegment);

    private readonly Dictionary<string, List<Point>> _sites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingBreaks = new(StringComparer.Ordinal);
    private string? _worldId;
    private string? _runId;
    private const int MaxSites = 32;
    private const int MaxPointsPerSite = 120;
    private const double MaxContinuousUtStep = 120;

    public void SetContext(string? worldId, string? runId)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(runId))
        {
            _worldId = null;
            _runId = null;
            _sites.Clear();
            _pendingBreaks.Clear();
            return;
        }
        if (worldId == _worldId && runId == _runId) return;
        _worldId = worldId;
        _runId = runId;
        _sites.Clear();
        _pendingBreaks.Clear();
    }

    public void Interrupt(string? siteKey)
    {
        if (siteKey is not null) _pendingBreaks.Add(siteKey);
    }

    public void Observe(string siteKey, double ut, double? generationEcPerSecond,
        double? consumptionEcPerSecond, double? netEcPerSecond, string coverageKey)
    {
        if (_worldId is null || _runId is null || !double.IsFinite(ut)) return;
        if (!_sites.TryGetValue(siteKey, out var points))
        {
            if (_sites.Count == MaxSites)
            {
                var oldest = _sites.Keys.First();
                _sites.Remove(oldest);
                _pendingBreaks.Remove(oldest);
            }
            points = new List<Point>();
            _sites.Add(siteKey, points);
        }

        if (points.Count > 0 && ut < points[^1].Ut)
        {
            points.Clear();
            _pendingBreaks.Remove(siteKey);
        }
        if (generationEcPerSecond is not double generation || !double.IsFinite(generation) ||
            consumptionEcPerSecond is not double consumption || !double.IsFinite(consumption) ||
            netEcPerSecond is not double net || !double.IsFinite(net))
        {
            _pendingBreaks.Add(siteKey);
            return;
        }
        if (points.Count > 0 && ut == points[^1].Ut) return;

        bool interrupted = _pendingBreaks.Remove(siteKey);
        bool startsSegment = points.Count == 0 || interrupted || ut - points[^1].Ut > MaxContinuousUtStep ||
            !string.Equals(points[^1].CoverageKey, coverageKey, StringComparison.Ordinal);
        points.Add(new Point(ut, generation, consumption, net, coverageKey, startsSegment));
        if (points.Count > MaxPointsPerSite)
        {
            points.RemoveRange(0, points.Count - MaxPointsPerSite);
            points[0] = points[0] with { StartsSegment = true };
        }
    }

    public Point[] ForSite(string? siteKey) => siteKey is not null && _sites.TryGetValue(siteKey, out var points)
        ? points.ToArray() : Array.Empty<Point>();
}
