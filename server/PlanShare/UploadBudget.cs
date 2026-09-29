using System.Collections.Concurrent;

namespace PlanShare;

/// <summary>
/// Caps how many bytes of plan data one client key can store per UTC day. In memory like
/// RateLimiter, so a restart forgets it. A charge is made before the insert and is not refunded
/// if the insert then fails: refunding would need a second code path for a rare error, and the
/// most a client can lose that way is one upload's worth of its allowance.
/// </summary>
internal sealed class UploadBudget
{
    private sealed class Usage
    {
        public DateOnly Day;
        public long Bytes;
        public bool Evicted;
    }

    private readonly long _dailyLimitBytes;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Usage> _usage = new();

    public UploadBudget(long dailyLimitBytes, TimeProvider? clock = null)
    {
        _dailyLimitBytes = dailyLimitBytes;
        _clock = clock ?? TimeProvider.System;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Adds <paramref name="bytes"/> to the key's total for today and returns true, or returns
    /// false and adds nothing when that would take the total over the daily limit.
    /// </summary>
    public bool TryCharge(string key, long bytes)
    {
        var today = Today();

        while (true)
        {
            var usage = _usage.GetOrAdd(key, _ => new Usage { Day = today });

            lock (usage)
            {
                // Sweep() removed this entry between GetOrAdd and the lock; charging it now
                // would be lost, so fetch or create the live one.
                if (usage.Evicted)
                    continue;

                if (usage.Day != today)
                {
                    usage.Day = today;
                    usage.Bytes = 0;
                }

                if (usage.Bytes + bytes > _dailyLimitBytes)
                    return false;

                usage.Bytes += bytes;
                return true;
            }
        }
    }

    /// <summary>
    /// Evicts keys whose last charge was on an earlier UTC day. Call periodically so the
    /// dictionary doesn't grow forever across unique clients.
    /// Returns the number of keys evicted.
    /// </summary>
    public int Sweep()
    {
        var today = Today();
        var evicted = 0;
        foreach (var kvp in _usage)
        {
            lock (kvp.Value)
            {
                if (kvp.Value.Day != today && _usage.TryRemove(kvp))
                {
                    kvp.Value.Evicted = true;
                    evicted++;
                }
            }
        }
        return evicted;
    }
}
