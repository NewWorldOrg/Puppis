using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;

namespace Puppis.Driver.Tests;

public sealed class ClaimedConnection(WatchedConnection watched, Action usedAfterClosing) : IUsbConnection
{
    private volatile bool disposed;

    public string Path => watched.Path;

    public bool Disposed => disposed;

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout)
    {
        RequireOpen();

        return watched.ControlIn(setup, data, timeout);
    }

    public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        RequireOpen();
        watched.ControlOut(setup, data, timeout);
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        watched.ReadBulk(endpoint, transferBytes, transfersInFlight);

    public void Dispose() => disposed = true;

    private void RequireOpen()
    {
        if (disposed)
        {
            usedAfterClosing();

            throw DvbFailure.Refused($"{Path}: the device was asked after it was closed.");
        }
    }
}
