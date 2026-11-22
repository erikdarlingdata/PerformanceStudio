using System.Collections.Concurrent;

namespace PlanShare;

/// <summary>
/// Caps how many bytes of plan data can be stored per UTC day, for each key it is given. The
/// share endpoint keeps two: one keyed by client, and one with a single fixed key that caps all
/// clients together. In memory like RateLimiter, so a restart forgets it. A charge is made before
/// the insert and is not refunded if the insert then fails: that is a rare error, and the most a
/// client can lose is one upload's worth of its allowance. <see cref="Refund"/> exists for the
/// one case that is not rare: a share that passed one budget and was refused by the other.
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
    /// Takes back a charge just made with <see cref="TryCharge"/>. Does nothing if the key has no
    /// usage today (Sweep() removed it, or the day rolled over since the charge): that allowance
    /// is already gone. The total never goes below zero.
    /// </summary>
    public void Refund(string key, long bytes)
    {
        if (!_usage.TryGetValue(key, out var usage))
            return;

        lock (usage)
        {
            if (usage.Evicted || usage.Day != Today())
                return;

            usage.Bytes = Math.Max(0, usage.Bytes - bytes);
        }
    }

    /// <summary>
    /// Returns whether <see cref="TryCharge"/> would accept <paramref name="bytes"/> now, without
    /// charging. Lets the share endpoint refuse a declared size before reading the body; the real
    /// charge still happens after the read, because the declared size can be absent or wrong.
    /// </summary>
    public bool CanCharge(string key, long bytes)
    {
        if (!_usage.TryGetValue(key, out var usage))
            return bytes <= _dailyLimitBytes;

        lock (usage)
        {
            // An entry from an earlier day (or one Sweep() just evicted) starts again at zero
            var used = usage.Day == Today() && !usage.Evicted ? usage.Bytes : 0;
            return used + bytes <= _dailyLimitBytes;
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
