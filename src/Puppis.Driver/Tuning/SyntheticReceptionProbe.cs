using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning;

public sealed class SyntheticReceptionProbe(int physicalChannel) : IReceptionProbe
{
    private readonly FakeTunerDevice synthetic = new(physicalChannel);

    public SignalQuality Measure() => synthetic.Measure();

    public void Dispose() => synthetic.Dispose();
}
