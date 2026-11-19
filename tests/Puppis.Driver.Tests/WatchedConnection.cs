using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;

namespace Puppis.Driver.Tests;

public sealed class WatchedConnection(IUsbConnection inner) : IUsbConnection
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly List<WatchedRequest> requests = [];
    private readonly ManualResetEventSlim holding = new();
    private readonly ManualResetEventSlim letGo = new();
    private Func<WatchedRequest, bool>? heldAt;
    private Func<WatchedRequest, bool>? failingAt;
    private int atTheDevice;
    private int mostAtOnce;
    private long bulkTakes;
    private volatile bool bulkClosed;
    private volatile bool disposed;

    public string Path => inner.Path;

    public int MostAtOnce => Volatile.Read(ref mostAtOnce);

    public long BulkTakes => Interlocked.Read(ref bulkTakes);

    public bool BulkClosed => bulkClosed;

    public bool Disposed => disposed;

    public IReadOnlyList<WatchedRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public IReadOnlyList<(int Thread, int Count)> Runs()
    {
        List<(int Thread, int Count)> runs = [];

        foreach (WatchedRequest request in Requests)
        {
            if (runs.Count > 0 && runs[^1].Thread == request.Thread)
            {
                runs[^1] = (request.Thread, runs[^1].Count + 1);
            }
            else
            {
                runs.Add((request.Thread, 1));
            }
        }

        return runs;
    }

    public void HoldAt(Func<WatchedRequest, bool> which) => Volatile.Write(ref heldAt, which);

    public void FailAt(Func<WatchedRequest, bool>? which) => Volatile.Write(ref failingAt, which);

    public bool WaitUntilHolding() => holding.Wait(Patience);

    public void LetGo() => letGo.Set();

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout)
    {
        Arrive(setup);

        try
        {
            return inner.ControlIn(setup, data, timeout);
        }
        finally
        {
            Interlocked.Decrement(ref atTheDevice);
        }
    }

    public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        Arrive(setup);

        try
        {
            inner.ControlOut(setup, data, timeout);
        }
        finally
        {
            Interlocked.Decrement(ref atTheDevice);
        }
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        new WatchedBulkReader(() => Interlocked.Increment(ref bulkTakes), () => bulkClosed = true);

    public void Dispose()
    {
        disposed = true;
        inner.Dispose();
    }

    private void Arrive(UsbSetup setup)
    {
        var request = new WatchedRequest(Environment.CurrentManagedThreadId, setup);
        int now = Interlocked.Increment(ref atTheDevice);
        InterlockedMax(ref mostAtOnce, now);

        lock (requests)
        {
            requests.Add(request);
        }

        try
        {
            Thread.Yield();
            HoldOrFail(request);
        }
        catch
        {
            Interlocked.Decrement(ref atTheDevice);

            throw;
        }
    }

    private void HoldOrFail(WatchedRequest request)
    {
        if (Volatile.Read(ref heldAt) is { } held && held(request))
        {
            Volatile.Write(ref heldAt, null);
            holding.Set();
            letGo.Wait(Patience);
        }

        if (Volatile.Read(ref failingAt) is { } failing && failing(request))
        {
            throw DvbFailure.Refused($"{Path}: the request at 0x{request.Address:X} was made to fail.");
        }
    }

    private static void InterlockedMax(ref int most, int now)
    {
        int seen;

        do
        {
            seen = Volatile.Read(ref most);
        } while (now > seen && Interlocked.CompareExchange(ref most, now, seen) != seen);
    }
}
