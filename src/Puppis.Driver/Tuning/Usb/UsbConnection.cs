namespace Puppis.Driver.Tuning.Usb;

public readonly record struct UsbSetup(byte RequestType, byte Request, ushort Value, ushort Index);

public readonly record struct UsbModel(ushort VendorId, ushort ProductId)
{
    public override string ToString() => $"{VendorId:X4}:{ProductId:X4}";
}

public sealed record UsbDeviceNode(string Path, UsbModel Model, string Port);

public interface IUsbHost
{
    IReadOnlyList<UsbDeviceNode> List();

    UsbModel ModelAt(string path);

    IUsbConnection Open(string path, byte interfaceNumber);
}

public interface IUsbConnection : IDisposable
{
    string Path { get; }

    int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout);

    void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout);

    IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight);
}

public interface IUsbBulkReader : IDisposable
{
    int Take(Span<byte> into, TimeSpan patience);
}
