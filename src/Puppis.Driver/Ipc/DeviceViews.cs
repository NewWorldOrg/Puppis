using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Ipc;

public static class DeviceViews
{
    public static IReadOnlyList<DetectedDeviceDto> Detected(
        IReadOnlyList<TunerDetection> detections,
        TunerHardwareBook? hardware = null
    ) =>
        [
            .. detections.Select(detection => new DetectedDeviceDto
            {
                DeviceId = detection.DeviceId,
                Detection = detection.Detection,
                Kinds = [.. detection.Receives.Select(Wire)],
                Detail = detection.Detail,
                Hardware = hardware?.Known(detection.DeviceId),
            }),
        ];

    public static TunerKind Wire(DeviceKind kind) =>
        kind switch
        {
            DeviceKind.Terrestrial => TunerKind.Terrestrial,
            DeviceKind.Satellite => TunerKind.Satellite,
            _ => TunerKind.Unspecified,
        };
}
