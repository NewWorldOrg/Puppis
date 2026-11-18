using Microsoft.Extensions.Logging;

using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295ReceptionProbe : IReceptionProbe
{
    private readonly Dt295Bridge bridge;
    private readonly Action<Dt295Bridge>? keep;
    private readonly ILogger? logger;
    private readonly Lock gate = new();
    private bool closed;
    private bool faulted;

    private Dt295ReceptionProbe(Dt295Bridge bridge, Action<Dt295Bridge>? keep, ILogger? logger)
    {
        this.bridge = bridge;
        this.keep = keep;
        this.logger = logger;
    }

    public static Dt295ReceptionProbe Tune(
        Dt295Bridge bridge,
        DvbChannel channel,
        ILogger? logger,
        Action<Dt295Bridge>? keep
    )
    {
        try
        {
            Dt295TunerDevice.Tune(bridge, channel);

            return new Dt295ReceptionProbe(bridge, keep, logger);
        }
        catch (DvbDeviceException failed) when (failed.Failure is TuningFailure.NoLock)
        {
            Dt295TunerDevice.LetGo(bridge, keep, faulted: false, logger);

            throw;
        }
        catch
        {
            Dt295TunerDevice.LetGo(bridge, keep, faulted: true, logger);

            throw;
        }
    }

    public SignalQuality Measure()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);

            try
            {
                return Dt295TunerDevice.QualityOf(bridge.Frontend);
            }
            catch (DvbDeviceException)
            {
                faulted = true;

                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed)
            {
                return;
            }

            closed = true;
        }

        Dt295TunerDevice.LetGo(bridge, keep, faulted, logger);
    }
}
