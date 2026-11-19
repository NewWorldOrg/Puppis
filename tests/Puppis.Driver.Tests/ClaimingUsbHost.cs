using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class ClaimingUsbHost(WatchedConnection watched) : IUsbHost
{
    private const int Busy = 16;

    private readonly Lock gate = new();
    private ClaimedConnection? claimed;
    private long opened;
    private long usedAfterClosing;

    public long Opened => Interlocked.Read(ref opened);

    public long UsedAfterClosing => Interlocked.Read(ref usedAfterClosing);

    public IReadOnlyList<UsbDeviceNode> List() => [];

    public UsbModel ModelAt(string path) => CechZd1TunerDevice.Model;

    public IUsbConnection Open(string path, byte interfaceNumber)
    {
        lock (gate)
        {
            if (claimed is { Disposed: false })
            {
                throw DvbFailure.AtDevice(path, "claiming the interface", Busy, "Something else has the device open.");
            }

            claimed = new ClaimedConnection(watched, () => Interlocked.Increment(ref usedAfterClosing));
            Interlocked.Increment(ref opened);

            return claimed;
        }
    }
}
