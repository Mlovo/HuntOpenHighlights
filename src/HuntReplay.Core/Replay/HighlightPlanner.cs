using HuntReplay.Core.Configuration;

namespace HuntReplay.Core.Replay;

public sealed record HighlightEvent(string SessionId, string RequestId, string GroupId, string HighlightId,
    long AtUs, int StartDeltaMs = 0, int EndDeltaMs = 0);

public sealed class ClipPlan
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string SessionId { get; init; }
    public required string GroupId { get; init; }
    public required long FirstEventUs { get; init; }
    public long LastEventUs { get; set; }
    public long StartUs { get; set; }
    public long EndUs { get; set; }
    public long ReadyAtUs { get; set; }
    public List<string> Events { get; } = [];
    public int KillCount => Events.Count(e => e == "HUNTER_KILLED");
    public string EventTag
    {
        get
        {
            var tags = new List<string>();
            if (KillCount > 1) tags.Add($"MULTI_KILL_x{KillCount}");
            else if (KillCount == 1) tags.Add("HUNTER_KILLED");
            tags.AddRange(Events.Where(e => e != "HUNTER_KILLED").Distinct(StringComparer.Ordinal));
            return string.Join("_", tags);
        }
    }
}

/// <summary>
/// Deterministic event-to-clip planning. Uses the capture's monotonic clock, not wall-clock time.
/// Coalescing waits for the gap horizon (15s) but ends the actual video at last kill + post-roll (7s).
/// That distinction allows kills 10–15s apart to really merge without adding eight seconds of dead air.
/// The caller serializes access, owns RAM leases and schedules the ready plans.
/// </summary>
public sealed class HighlightPlanner(ReplaySettings settings)
{
    private readonly List<ClipPlan> pending = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    public int PendingCount => pending.Count;

    public ClipPlan? Register(HighlightEvent evt)
    {
        if (!settings.IsEnabled(evt.HighlightId)) return null;
        var dedup = evt.SessionId + "\0" + evt.RequestId;
        if (seen.Contains(dedup)) return null;
        if (evt.AtUs < 0) throw new ArgumentOutOfRangeException(nameof(evt));
        // Never trust game-provided deltas to reserve unbounded RAM.
        var preMs = evt.StartDeltaMs < 0
            ? Math.Min(-(long)evt.StartDeltaMs, settings.BufferSeconds * 1000L)
            : settings.PreEventSeconds * 1000L;
        var start = settings.SmartTrim ? evt.AtUs - preMs * 1000 : evt.AtUs - settings.BufferSeconds * 1_000_000L;
        var postUs = (long)(Math.Max(settings.PostEventSeconds,
            Math.Clamp(evt.EndDeltaMs / 1000d, 0, 30)) * 1_000_000);
        var end = evt.AtUs + postUs;
        var gapUs = settings.CoalesceGapSeconds * 1_000_000L;
        var plan = settings.Coalesce ? pending.LastOrDefault(p =>
            p.SessionId == evt.SessionId && p.GroupId == evt.GroupId &&
            evt.AtUs >= p.LastEventUs && evt.AtUs - p.LastEventUs <= gapUs &&
            evt.AtUs - p.FirstEventUs <= settings.MaxSequenceSeconds * 1_000_000L) : null;
        if (plan is null)
        {
            // Bound outstanding leases even if an untrusted pipe peer floods new groups.
            if (pending.Count >= 8) throw new InvalidOperationException("Очередь событий заполнена (8 клипов).");
            plan = new ClipPlan
            {
                SessionId = evt.SessionId, GroupId = evt.GroupId,
                FirstEventUs = evt.AtUs, StartUs = Math.Max(start, 0)
            };
            pending.Add(plan);
        }
        if (plan.Events.Count >= 256) throw new InvalidOperationException("Слишком много событий в одной серии (256).");
        seen.Add(dedup);
        seenOrder.Enqueue(dedup);
        while (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
        plan.StartUs = Math.Max(0, Math.Min(plan.StartUs, start));
        plan.LastEventUs = evt.AtUs;
        plan.EndUs = Math.Max(plan.EndUs, end);
        plan.ReadyAtUs = Math.Max(plan.EndUs, evt.AtUs + (settings.Coalesce ? gapUs : 0));
        plan.Events.Add(evt.HighlightId);
        return plan;
    }

    public IReadOnlyList<ClipPlan> TakeReady(long nowUs, bool force = false, int maximum = int.MaxValue)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ready = pending.Where(p => force || p.ReadyAtUs <= nowUs).Take(maximum).ToArray();
        foreach (var plan in ready) pending.Remove(plan);
        return ready;
    }
}
