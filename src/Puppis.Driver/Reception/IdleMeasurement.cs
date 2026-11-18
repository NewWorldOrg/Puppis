using Puppis.Driver.Sessions;

namespace Puppis.Driver.Reception;

public enum IdleMeasurementEnd
{
    Kept,
    ThrownAway,
    Interrupted,
    NotOpened,
    DeviceFailed,
}

public sealed record IdleMeasurement(string DeviceId, TuningKey Channel, IdleMeasurementEnd End, double? CarrierToNoise = null);
