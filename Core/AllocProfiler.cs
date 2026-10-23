using System.Diagnostics.Tracing;

namespace GlassIsland.Core;

/// <summary>
/// Benchmark-only allocation sampler: listens to the runtime's AllocationTick events (one per ~100 KB
/// allocated) and tallies them by type, so per-frame garbage can be traced to its source.
/// </summary>
public sealed class AllocProfiler : EventListener
{
    readonly Dictionary<string, int> _ticks = [];
    public bool Recording { get; set; }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!Recording || e.EventName is not "GCAllocationTick_V4" and not "GCAllocationTick_V3" || e.Payload == null) return;
        int i = e.PayloadNames!.IndexOf("TypeName");
        if (i < 0) return;
        var type = e.Payload[i] as string ?? "?";
        lock (_ticks) _ticks[type] = _ticks.GetValueOrDefault(type) + 1;
    }

    public string Top(int n)
    {
        lock (_ticks)
            return string.Join("\n", _ticks.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"  {kv.Value,5} × ~100KB  {kv.Key}"));
    }
}
