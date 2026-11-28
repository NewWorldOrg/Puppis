using Puppis.Driver.Tuning;

namespace Puppis.Driver.Sessions;

public sealed class LeasedTunerDevice(ITunerDevice tuner) : ITunerDevice
{
    public long Overflows => tuner.Overflows;

    public ISignalQualitySource? Quality => tuner.Quality;

    public bool CarriesEveryProgramme => tuner.CarriesEveryProgramme;

    public byte[] Read(int count, CancellationToken cancellationToken) =>
        tuner.Read(count, cancellationToken);

    public void Dispose() { }
}
