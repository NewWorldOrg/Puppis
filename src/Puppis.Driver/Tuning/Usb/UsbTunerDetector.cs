using Puppis.Contracts;
using Puppis.Driver.Configuration;

namespace Puppis.Driver.Tuning.Usb;

public sealed class UsbTunerDetector : ITunerDetector
{
    private readonly Lazy<IUsbHost> host;

    public UsbTunerDetector()
        : this(new Lazy<IUsbHost>(() => new LinuxUsbHost())) { }

    private UsbTunerDetector(Lazy<IUsbHost> host)
    {
        this.host = host;
    }

    public static UsbTunerDetector Using(IUsbHost host) => new(new Lazy<IUsbHost>(() => host));

    public static string IdOf(UsbDeviceNode node) => $"usb{node.Port}";

    public static string? NodeFor(IUsbHost host, string? deviceId, string? recordedPath) =>
        host.List()
            .FirstOrDefault(node =>
                UsbTunerModels.Knows(node.Model) && string.Equals(IdOf(node), deviceId, StringComparison.Ordinal))
            ?.Path
        ?? recordedPath;

    public IReadOnlyList<TunerDetection> Detect() =>
    [
        .. host.Value.List()
            .Select(node => (Node: node, Known: UsbTunerModels.Find(node.Model)))
            .Where(found => found.Known is not null)
            .Select(found => new TunerDetection(
                IdOf(found.Node),
                [.. found.Known!.Receives],
                DeviceDetection.Detected,
                null,
                found.Node.Path,
                new DeviceHardware(found.Known.ProductName, null, TunerBus.Usb, found.Node.Port)
            )),
    ];
}
