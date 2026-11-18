using Microsoft.Extensions.Logging;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

public sealed class CechZd1ReceptionProbe : IReceptionProbe
{
    private readonly CechZd1Standby.KeptTuner tuner;
    private readonly CechZd1Standby? keptIn;
    private readonly Lock gate = new();
    private bool closed;

    private CechZd1ReceptionProbe(CechZd1Standby.KeptTuner tuner, CechZd1Standby? keptIn)
    {
        this.tuner = tuner;
        this.keptIn = keptIn;
    }

    public static CechZd1ReceptionProbe Open(
        IUsbHost host,
        string path,
        long hertz,
        DevicePacing pacing,
        ILogger? logger = null,
        CechZd1Standby? standby = null
    )
    {
        CechZd1TunerDevice.Recognise(host, path);

        CechZd1Standby.KeptTuner? kept = standby?.Take(path);
        CechZd1Standby? keptIn = kept is null ? null : standby;

        if (kept is not null && Woken(kept, hertz, keptIn, logger) is { } woken)
        {
            return woken;
        }

        return Tuned(Started(host, path, pacing), hertz, keptIn);
    }

    private static CechZd1ReceptionProbe? Woken(
        CechZd1Standby.KeptTuner kept,
        long hertz,
        CechZd1Standby? keptIn,
        ILogger? logger
    )
    {
        try
        {
            if (kept.Frontend.Tune(hertz) is not SearchOutcome.Failed)
            {
                return new CechZd1ReceptionProbe(kept, keptIn);
            }
        }
        catch (DvbDeviceException fault)
        {
            logger?.LogWarning(
                fault,
                "{Path}: the tuner kept open did not answer while its reception was being measured, so it is started afresh.",
                kept.Path
            );
        }

        CechZd1TunerDevice.Quietly(kept.Frontend.Sleep);
        CechZd1TunerDevice.Quietly(() => kept.Frontend.Demodulator.LedGreen());
        kept.Usb.Dispose();

        return null;
    }

    private static CechZd1Standby.KeptTuner Started(IUsbHost host, string path, DevicePacing pacing)
    {
        IUsbConnection usb = host.Open(path, CechZd1TunerDevice.Interface);

        try
        {
            var memory = new BridgeMemory(usb);
            BridgeStartup.Start(memory, pacing);
            var i2c = new BridgeI2c(memory);
            var frontend = new CechZd1Frontend(new Dib8000Bus(i2c), new Dib0090Bus(i2c), pacing, path);
            frontend.Initialise();

            return new CechZd1Standby.KeptTuner(path, usb, memory, frontend, new TransportCapture(memory));
        }
        catch
        {
            usb.Dispose();

            throw;
        }
    }

    private static CechZd1ReceptionProbe Tuned(CechZd1Standby.KeptTuner started, long hertz, CechZd1Standby? keptIn)
    {
        SearchOutcome outcome;

        try
        {
            outcome = started.Frontend.Tune(hertz);
        }
        catch
        {
            started.Usb.Dispose();

            throw;
        }

        if (outcome is not SearchOutcome.Failed)
        {
            return new CechZd1ReceptionProbe(started, keptIn);
        }

        CechZd1TunerDevice.Rest(started, keptIn);

        throw DvbFailure.NoLock(
            $"{started.Path}: the demodulator did not synchronise on {hertz / 1000} kHz while its reception was being measured."
        );
    }

    public SignalQuality Measure()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);

            return CechZd1Signal.QualityOf(tuner.Frontend.ReadSignal());
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
            CechZd1TunerDevice.Rest(tuner, keptIn);
        }
    }
}
