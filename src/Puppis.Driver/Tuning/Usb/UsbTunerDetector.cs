using Puppis.Contracts;
using Puppis.Driver.Configuration;

namespace Puppis.Driver.Tuning.Usb;

/// <summary>
/// Finds the USB tuners this driver knows among the devices the host can read. The device id follows the port the
/// tuner hangs on, which a replug into the same socket keeps.
/// </summary>
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

    /// <summary>
    /// Finds the node the tuner with this id hangs on now. The node number changes whenever the device is plugged in
    /// again, so the recorded path is only used for an id no port answers to.
    /// </summary>
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
