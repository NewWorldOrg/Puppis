using Puppis.Driver.Sessions;

namespace Puppis.Driver.Reception;

public enum IdleMeasurementEnd
{
    Kept,
    ThrownAway,
    Interrupted,
    DeviceFailed,
    Refused,
}

public sealed record IdleMeasurement(string DeviceId, TuningKey Channel, IdleMeasurementEnd End, double? CarrierToNoise = null);
