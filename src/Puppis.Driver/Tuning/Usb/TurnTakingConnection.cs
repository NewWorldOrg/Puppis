namespace Puppis.Driver.Tuning.Usb;

/// <summary>
/// Hands the control endpoint to its callers one turn at a time and in the order they asked, so that a caller sending
/// a long run of requests keeps another waiting for no more than one turn. A turn is one request, or the requests a
/// caller asks to make together.
/// </summary>
public sealed class TurnTakingConnection(IUsbConnection usb) : IUsbConnection
{
    private readonly object turns = new();
    private long issued;
    private long serving;
    private Thread? holder;

    public string Path => usb.Path;

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout)
    {
        if (Holds())
        {
            return usb.ControlIn(setup, data, timeout);
        }

        Wait();

        try
        {
            return usb.ControlIn(setup, data, timeout);
        }
        finally
        {
            Pass();
        }
    }

    public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        if (Holds())
        {
            usb.ControlOut(setup, data, timeout);

            return;
        }

        Wait();

        try
        {
            usb.ControlOut(setup, data, timeout);
        }
        finally
        {
            Pass();
        }
    }

    /// <summary>
    /// Makes <paramref name="requests"/> in one turn, so that nobody else's request comes between them.
    /// </summary>
    public T Together<T>(Func<T> requests)
    {
        Wait();

        try
        {
            holder = Thread.CurrentThread;

            return requests();
        }
        finally
        {
            holder = null;
            Pass();
        }
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        usb.ReadBulk(endpoint, transferBytes, transfersInFlight);

    public void Dispose() => usb.Dispose();

    private bool Holds() => ReferenceEquals(Volatile.Read(ref holder), Thread.CurrentThread);

    private void Wait()
    {
        lock (turns)
        {
            long mine = issued++;

            while (mine != serving)
            {
                Monitor.Wait(turns);
            }
        }
    }

    private void Pass()
    {
        lock (turns)
        {
            serving++;
            Monitor.PulseAll(turns);
        }
    }
}
