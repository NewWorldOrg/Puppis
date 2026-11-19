using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tests;

public sealed record WatchedRequest(int Thread, UsbSetup Setup)
{
    public uint Address => FakeBridge.AddressOf(Setup);

    public bool Writes => Setup.RequestType is BridgeMemory.WriteRequestType;
}
