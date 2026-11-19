using Microsoft.Extensions.Logging;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

public sealed class CechZd1ReceptionProbe : IReceptionProbe
{
    private readonly CechZd1Standby.KeptTuner tuner;
    private readonly CechZd1Standby? keptIn;
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
        CechZd1Standby? standby = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CechZd1TunerDevice.Recognise(host, path);

        CechZd1Standby.KeptTuner? kept = standby?.Take(path);
        CechZd1Standby? keptIn = kept is null ? null : standby;

        if (kept is not null && Woken(kept, hertz, keptIn, logger, cancellationToken) is { } woken)
        {
            return woken;
        }

        CechZd1Standby.KeptTuner started = Started(
            host,
            path,
            pacing,
            keptIn is null ? cancellationToken : CancellationToken.None
        );

        if (cancellationToken.IsCancellationRequested)
        {
            CechZd1TunerDevice.Rest(started, keptIn);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Tuned(started, hertz, keptIn);
    }

    private static CechZd1ReceptionProbe? Woken(
        CechZd1Standby.KeptTuner kept,
        long hertz,
        CechZd1Standby? keptIn,
        ILogger? logger,
        CancellationToken cancellationToken
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

        if (cancellationToken.IsCancellationRequested)
        {
            CechZd1TunerDevice.Rest(kept, keptIn);
            cancellationToken.ThrowIfCancellationRequested();
        }

        Close(kept.Frontend, kept.Usb);

        return null;
    }

    private static void Close(CechZd1Frontend? frontend, TurnTakingConnection usb)
    {
        if (frontend is not null)
        {
            CechZd1TunerDevice.Quietly(frontend.Sleep);
            CechZd1TunerDevice.Quietly(() => frontend.Demodulator.LedGreen());
        }

        usb.Dispose();
    }

    private static CechZd1Standby.KeptTuner Started(
        IUsbHost host,
        string path,
        DevicePacing pacing,
        CancellationToken stopWhileStillClosed
    )
    {
        var usb = new TurnTakingConnection(host.Open(path, CechZd1TunerDevice.Interface));
        CechZd1Frontend? frontend = null;

        try
        {
            stopWhileStillClosed.ThrowIfCancellationRequested();
            var memory = new BridgeMemory(usb);
            BridgeStartup.Start(memory, pacing);
            stopWhileStillClosed.ThrowIfCancellationRequested();
            var i2c = new BridgeI2c(memory);
            frontend = new CechZd1Frontend(new Dib8000Bus(i2c), new Dib0090Bus(i2c), pacing, path);
            frontend.Initialise();

            return new CechZd1Standby.KeptTuner(path, usb, memory, frontend, new TransportCapture(memory));
        }
        catch
        {
            Close(frontend, usb);

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
            Close(started.Frontend, started.Usb);

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
        using HeldTurn turn = tuner.Usb.TakeTurn();
        ObjectDisposedException.ThrowIf(closed, this);

        return CechZd1Signal.QualityOf(tuner.Frontend.ReadSignal());
    }

    public void Dispose()
    {
        using HeldTurn turn = tuner.Usb.TakeTurn();

        if (closed)
        {
            return;
        }

        closed = true;
        CechZd1TunerDevice.Rest(tuner, keptIn);
    }
}
