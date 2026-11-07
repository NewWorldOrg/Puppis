using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

/// <summary>
/// The bridge and the frontend of a tuner that was closed, kept open and asleep so that the next session on the same
/// device wakes and tunes them instead of starting the bridge and initialising the chips again.
/// </summary>
public sealed class CechZd1Standby : IDisposable
{
    private readonly Lock gate = new();
    private KeptTuner? kept;
    private bool disposed;

    public sealed record KeptTuner(
        string Path,
        IUsbConnection Usb,
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

    /// <summary>
    /// Hands out what is kept for <paramref name="path"/>, or null when nothing is. Whatever is kept for another path
    /// is closed, since the tuner it belonged to is no longer where it was.
    /// </summary>
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

    /// <summary>
    /// Keeps the tuner for the next session. Anything kept before, and anything given after this standby is disposed,
    /// is closed instead.
    /// </summary>
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

    /// <summary>
    /// Asks the kept demodulator to identify itself, which is the check a closed device would get by being opened.
    /// </summary>
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
