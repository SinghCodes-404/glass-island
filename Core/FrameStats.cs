using System.Diagnostics;
using System.Text;
using System.Windows.Media;

namespace GlassIsland.Core;

/// <summary>
/// Benchmark helper (--bench): records frame intervals from CompositionTarget.Rendering, time spent in
/// backdrop capture, and process CPU per named phase.
/// </summary>
public sealed class FrameStats
{
    readonly List<double> _frames = [];
    readonly List<double> _captures = [];
    readonly StringBuilder _report = new();
    TimeSpan _last;
    string _phase = "";
    TimeSpan _cpuStart;
    int _gen2Start, _gen0Start;
    long _allocStart;
    readonly Stopwatch _wall = new();

    public void Attach() => CompositionTarget.Rendering += OnRendering;
    public void Detach() => CompositionTarget.Rendering -= OnRendering;

    void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _last) return;
        if (_last != default) _frames.Add((t - _last).TotalMilliseconds);
        _last = t;
    }

    public void Begin(string phase)
    {
        _phase = phase;
        _frames.Clear();
        _captures.Clear();
        _last = default;
        using var p = Process.GetCurrentProcess();
        _cpuStart = p.TotalProcessorTime;
        _gen2Start = GC.CollectionCount(2);
        _gen0Start = GC.CollectionCount(0);
        _allocStart = GC.GetTotalAllocatedBytes();
        _wall.Restart();
    }

    public void End()
    {
        using var p = Process.GetCurrentProcess();
        double cpu = (p.TotalProcessorTime - _cpuStart).TotalMilliseconds / _wall.Elapsed.TotalMilliseconds * 100; // % of one core
        var f = _frames.OrderBy(x => x).ToList();
        double P(double q) => f.Count == 0 ? 0 : f[Math.Min(f.Count - 1, (int)(q * f.Count))];
        int janky = f.Count(x => x > 25), bad = f.Count(x => x > 50);
        _report.AppendLine(
            $"{_phase,-12} frames={f.Count,4} fps={(f.Count == 0 ? 0 : 1000 / f.Average()),5:F1} p50={P(0.5),5:F1}ms p95={P(0.95),5:F1}ms max={(f.Count == 0 ? 0 : f[^1]),6:F1}ms " +
            $">25ms={janky,3} >50ms={bad,3} | captures={_captures.Count,3} avg={(_captures.Count == 0 ? 0 : _captures.Average()),5:F2}ms | cpu={cpu,5:F1}% of a core | gc0={GC.CollectionCount(0) - _gen0Start} gc2={GC.CollectionCount(2) - _gen2Start} alloc={(GC.GetTotalAllocatedBytes() - _allocStart) / 1048576.0:F1}MB");
    }

    /// <summary>Called from the capture thread.</summary>
    public void AddCapture(double ms) { lock (_captures) _captures.Add(ms); }

    public override string ToString() => _report.ToString();
}
