namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295Keeper : IDisposable
{
    private readonly Dictionary<string, Dt295Bridge> kept = new(StringComparer.Ordinal);
    private readonly HashSet<string> barred = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private bool disposed;

    public bool Holds(string deviceId)
    {
        lock (gate)
        {
            return kept.ContainsKey(deviceId);
        }
    }

    public Dt295Bridge? Take(string deviceId, string path, StageTimes? stages = null)
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

        if (taken.Path == path && taken.IsOpen && taken.StillAsLeft(stages))
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
            closing = disposed || barred.Contains(deviceId);
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

    public void LetGo(string deviceId)
    {
        Dt295Bridge? letGo;

        lock (gate)
        {
            barred.Add(deviceId);
            kept.Remove(deviceId, out letGo);
        }

        letGo?.Close();
    }

    public void Admit(string deviceId)
    {
        lock (gate)
        {
            barred.Remove(deviceId);
        }
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
