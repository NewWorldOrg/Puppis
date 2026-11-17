namespace Puppis.Driver.Tuning.Usb;

/// <summary>
/// Hands the control endpoint to its callers one request at a time and in the order they asked, so that a caller
/// sending a long run of requests keeps another waiting for no more than one of them.
/// </summary>
public sealed class TurnTakingConnection(IUsbConnection usb) : IUsbConnection
{
    private readonly object turns = new();
    private long issued;
    private long serving;

    public string Path => usb.Path;

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout)
    {
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

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        usb.ReadBulk(endpoint, transferBytes, transfersInFlight);

    public void Dispose() => usb.Dispose();

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
