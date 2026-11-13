using Microsoft.Extensions.Logging;

using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Sessions;

/// <summary>
/// When a faulted device is next tried, and whether it failed again soon after it was last handed back out.
/// </summary>
public sealed record RecheckPlan(DateTimeOffset NextTryAt, bool Relapsed);

public sealed class FaultedTunerRecheck(
    ITunerDeviceCheck check,
    TimeProvider timeProvider,
    ILogger logger
) : IDisposable
{
    public static readonly IReadOnlyList<TimeSpan> Intervals =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    public static readonly TimeSpan RelapseWindow = TimeSpan.FromMinutes(30);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Restoration> restored = new(StringComparer.Ordinal);

    private bool disposed;

    public RecheckPlan? Schedule(DeviceSettings device, Action<string> restore)
    {
        string deviceId = device.Id!;
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            if (pending.TryGetValue(deviceId, out Pending? waiting))
            {
                return new RecheckPlan(waiting.NextTryAt, false);
            }

            int relapse = restored.TryGetValue(deviceId, out Restoration? last) && now - last.At <= RelapseWindow
                ? Math.Min(last.Relapse + 1, Intervals.Count - 1)
                : 0;
            Pending entry = new(device, restore, now + Intervals[relapse], relapse) { Tries = relapse };
            entry.Timer = timeProvider.CreateTimer(
                _ => TryAgain(entry),
                null,
                Intervals[relapse],
                Timeout.InfiniteTimeSpan
            );
            pending[deviceId] = entry;

            return new RecheckPlan(entry.NextTryAt, relapse > 0);
        }
    }

    public DateTimeOffset? NextTryOf(string deviceId)
    {
        lock (gate)
        {
            return pending.TryGetValue(deviceId, out Pending? waiting) ? waiting.NextTryAt : null;
        }
    }

    public void Forget(string deviceId)
    {
        Pending? dropped;

        lock (gate)
        {
            pending.Remove(deviceId, out dropped);
        }

        dropped?.Timer?.Dispose();
    }

    public void Dispose()
    {
        Pending[] dropped;

        lock (gate)
        {
            disposed = true;
            dropped = [.. pending.Values];
            pending.Clear();
        }

        foreach (Pending entry in dropped)
        {
            entry.Timer?.Dispose();
        }
    }

    private bool StillWaiting(Pending entry) =>
        !disposed
        && pending.TryGetValue(entry.Device.Id!, out Pending? current)
        && ReferenceEquals(current, entry);

    private void TryAgain(Pending entry)
    {
        string deviceId = entry.Device.Id!;

        lock (gate)
        {
            if (!StillWaiting(entry))
            {
                return;
            }
        }

        try
        {
            check.Check(entry.Device);
        }
        catch (Exception error)
        {
            StillFaulted(entry, error);

            return;
        }

        lock (gate)
        {
            if (!StillWaiting(entry))
            {
                return;
            }

            pending.Remove(deviceId);
            restored[deviceId] = new Restoration(timeProvider.GetUtcNow(), entry.Relapse);
        }

        entry.Timer?.Dispose();
        entry.Restore(deviceId);
    }

    private void StillFaulted(Pending entry, Exception error)
    {
        DateTimeOffset nextTryAt;

        lock (gate)
        {
            if (!StillWaiting(entry))
            {
                return;
            }

            entry.Tries++;
            TimeSpan wait = Intervals[Math.Min(entry.Tries, Intervals.Count - 1)];
            nextTryAt = entry.NextTryAt = timeProvider.GetUtcNow() + wait;
            entry.Timer?.Change(wait, Timeout.InfiniteTimeSpan);
        }

        logger.LogWarning(
            "The faulted device {DeviceId} still could not be opened when it was tried again ({Reason}); it stays faulted and is tried again at {NextTryAt}.",
            entry.Device.Id,
            error.Message,
            nextTryAt
        );
    }

    private sealed record Restoration(DateTimeOffset At, int Relapse);

    private sealed class Pending(DeviceSettings device, Action<string> restore, DateTimeOffset nextTryAt, int relapse)
    {
        public int Relapse { get; } = relapse;

        public DeviceSettings Device { get; } = device;

        public Action<string> Restore { get; } = restore;

        public DateTimeOffset NextTryAt { get; set; } = nextTryAt;

        public int Tries { get; set; }

        public ITimer? Timer { get; set; }
    }
}
