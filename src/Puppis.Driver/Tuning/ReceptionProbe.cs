using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning;

public interface IReceptionProbe : IDisposable
{
    SignalQuality Measure();
}

public interface IReceptionProbeFactory
{
    IReceptionProbe Tune(DeviceSettings device, TuneParams tune, CancellationToken cancellationToken);
}
