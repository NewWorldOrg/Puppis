namespace Puppis.Driver.Tuning.Usb;

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

    public T Together<T>(Func<T> requests)
    {
        using HeldTurn turn = TakeTurn();

        return requests();
    }

    public HeldTurn TakeTurn()
    {
        if (Holds())
        {
            return default;
        }

        long turn = Wait();
        Volatile.Write(ref holder, Thread.CurrentThread);

        return new HeldTurn(this, turn);
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        usb.ReadBulk(endpoint, transferBytes, transfersInFlight);

    public void Dispose() => usb.Dispose();

    internal void GiveBack(long turn)
    {
        lock (turns)
        {
            if (turn != serving)
            {
                return;
            }

            Volatile.Write(ref holder, null);
            serving++;
            Monitor.PulseAll(turns);
        }
    }

    private bool Holds() => ReferenceEquals(Volatile.Read(ref holder), Thread.CurrentThread);

    private long Wait()
    {
        lock (turns)
        {
            long mine = issued++;

            while (mine != serving)
            {
                Monitor.Wait(turns);
            }

            return mine;
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
