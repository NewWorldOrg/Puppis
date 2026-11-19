using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning.Usb.CechZd1;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tuning.Usb;

public sealed record UsbTunerModel(UsbModel Model, string ProductName, IReadOnlyList<DeviceKind> Receives);

public static class UsbTunerModels
{
    public static readonly IReadOnlyList<UsbTunerModel> All =
    [
        new(CechZd1TunerDevice.Model, CechZd1TunerDevice.ProductName, [DeviceKind.Terrestrial]),
        new(Dt295TunerDevice.Model, Dt295TunerDevice.ProductName, [DeviceKind.Terrestrial, DeviceKind.Satellite]),
    ];

    public static UsbTunerModel? Find(UsbModel model) => All.FirstOrDefault(known => known.Model == model);

    public static bool Knows(UsbModel model) => Find(model) is not null;
}
