using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class WatchedBulkReader(Action taken, Action closed) : IUsbBulkReader
{
    private static readonly byte[] Transfer = UsbTestSupport.Units(TransportCapture.TransferBytes / TransportCapture.UnitBytes);

    public int Take(Span<byte> into, TimeSpan patience)
    {
        taken();
        Transfer.CopyTo(into);

        return Transfer.Length;
    }

    public void Dispose() => closed();
}
