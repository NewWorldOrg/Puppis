using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class ProbeAsSource(IReceptionProbe probe) : ISignalQualitySource
{
    public SignalQuality Measure() => probe.Measure();
}
