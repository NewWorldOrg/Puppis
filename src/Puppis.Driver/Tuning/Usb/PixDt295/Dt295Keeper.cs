namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The PIX-DT295 bridges no session is on, kept open and ready by the id of the device they were declared as, so that
/// the next session on one only tunes and starts the stream. Whatever is let go of, or handed in after the keeper is
/// disposed, is closed and given back as it was found.
/// </summary>
public sealed class Dt295Keeper : IDisposable
{
    private readonly Dictionary<string, Dt295Bridge> kept = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private bool disposed;

    public bool Holds(string deviceId)
    {
        lock (gate)
        {
            return kept.ContainsKey(deviceId);
        }
    }

    /// <summary>
    /// The bridge kept for <paramref name="deviceId"/>, when it is still at <paramref name="path"/>, still as it was
    /// left and still open. A bridge that is not is closed, and null is the answer.
    /// </summary>
    public Dt295Bridge? Take(string deviceId, string path)
    {
        Dt295Bridge? taken;

        lock (gate)
        {
            kept.Remove(deviceId, out taken);
        }

        if (taken is null)
        {
            return null;
        }

        if (taken.Path == path && taken.IsOpen && taken.StillAsLeft())
        {
            return taken;
        }

        taken.Close();

        return null;
    }

    public void Keep(string deviceId, Dt295Bridge bridge)
    {
        Dt295Bridge? replaced;
        bool closing;

        lock (gate)
        {
            closing = disposed;
            kept.Remove(deviceId, out replaced);

            if (!closing)
            {
                kept[deviceId] = bridge;
            }
        }

        replaced?.Close();

        if (closing)
        {
            bridge.Close();
        }
    }

    /// <summary>
    /// Closes the bridge kept for <paramref name="deviceId"/>, if there is one.
    /// </summary>
    public void LetGo(string deviceId)
    {
        Dt295Bridge? letGo;

        lock (gate)
        {
            kept.Remove(deviceId, out letGo);
        }

        letGo?.Close();
    }

    public void Dispose()
    {
        List<Dt295Bridge> closing;

        lock (gate)
        {
            disposed = true;
            closing = [.. kept.Values];
            kept.Clear();
        }

        foreach (Dt295Bridge bridge in closing)
        {
            bridge.Close();
        }
    }
}
