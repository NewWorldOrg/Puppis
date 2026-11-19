using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

public sealed class CechZd1Standby : IDisposable
{
    private readonly Lock gate = new();
    private KeptTuner? kept;
    private bool disposed;

    public sealed record KeptTuner(
        string Path,
        TurnTakingConnection Usb,
        BridgeMemory Memory,
        CechZd1Frontend Frontend,
        TransportCapture Capture
    );

    public bool Holds(string path)
    {
        lock (gate)
        {
            return kept?.Path == path;
        }
    }

    public bool HoldsATuner
    {
        get
        {
            lock (gate)
            {
                return kept is not null;
            }
        }
    }

    public KeptTuner? Take(string path)
    {
        KeptTuner? taken;

        lock (gate)
        {
            taken = kept;
            kept = null;
        }

        if (taken is null)
        {
            return null;
        }

        if (taken.Path == path)
        {
            return taken;
        }

        taken.Usb.Dispose();

        return null;
    }

    public void Keep(KeptTuner tuner)
    {
        KeptTuner? replaced;

        lock (gate)
        {
            replaced = kept;
            kept = disposed ? null : tuner;
        }

        replaced?.Usb.Dispose();

        if (disposed)
        {
            tuner.Usb.Dispose();
        }
    }

    public void Check(string path)
    {
        KeptTuner? taken = Take(path);

        if (taken is null)
        {
            throw new InvalidOperationException($"Nothing is kept for {path}.");
        }

        try
        {
            taken.Frontend.Demodulator.Identify(path);
        }
        catch (DvbDeviceException)
        {
            taken.Usb.Dispose();

            throw;
        }

        Keep(taken);
    }

    public void Release()
    {
        KeptTuner? closing;

        lock (gate)
        {
            closing = kept;
            kept = null;
        }

        closing?.Usb.Dispose();
    }

    public void Dispose()
    {
        KeptTuner? closing;

        lock (gate)
        {
            disposed = true;
            closing = kept;
            kept = null;
        }

        closing?.Usb.Dispose();
    }
}
