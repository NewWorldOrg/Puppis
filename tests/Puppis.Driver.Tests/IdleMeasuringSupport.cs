using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class MeasuredHardware : ITunerDeviceFactory, IReceptionProbeFactory, ITunerDeviceCheck
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> busy = new(StringComparer.Ordinal);
    private readonly List<string> happened = [];
    private readonly List<string> overlaps = [];

    public Dictionary<(string DeviceId, int Channel), double[]> Signals { get; } = [];

    public HashSet<(string DeviceId, int Channel)> Dead { get; } = [];

    public HashSet<string> Broken { get; } = [];

    public Dictionary<string, int> BreaksAtRead { get; } = [];

    public HashSet<(string DeviceId, int Read)> Unreadable { get; } = [];

    public CancellationToken LastStopping { get; private set; }

    public Action<string>? WhileClosing { get; set; }

    public HashSet<string> Refusing { get; } = [];

    public bool TuneStopsWhenTold { get; set; }

    public Action<string>? BeforeOpening { get; set; }

    public Action<string>? WhileOpening { get; set; }

    public Action<string>? WhileTuning { get; set; }

    public Action<string, int>? WhileReading { get; set; }

    public IReadOnlyList<string> Happened
    {
        get
        {
            lock (gate)
            {
                return [.. happened];
            }
        }
    }

    public IReadOnlyList<string> Overlaps
    {
        get
        {
            lock (gate)
            {
                return [.. overlaps];
            }
        }
    }

    public int ProbesOpened => Happened.Count(line => line.StartsWith("probe opened", StringComparison.Ordinal));

    public IReceptionProbe Tune(DeviceSettings device, TuneParams tune, CancellationToken cancellationToken)
    {
        string deviceId = device.Id!;
        int channel = tune.ToLegacyRequest().PhysicalChannel;

        LastStopping = cancellationToken;
        BeforeOpening?.Invoke(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        Take(deviceId, $"probe opened {deviceId} {channel}");

        try
        {
            WhileOpening?.Invoke(deviceId);
            Fail(deviceId, channel);
            WhileTuning?.Invoke(deviceId);

            if (TuneStopsWhenTold)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            Release(deviceId, $"probe closed {deviceId}");

            throw;
        }

        return new Probe(this, deviceId, Signals.GetValueOrDefault((deviceId, channel)) ?? [20, 20, 20, 20, 20]);
    }

    private void Fail(string deviceId, int channel)
    {
        if (Refusing.Contains(deviceId))
        {
            throw DvbFailure.Refused($"{deviceId} does not receive channel {channel}.");
        }

        if (Broken.Contains(deviceId))
        {
            throw DvbFailure.AtDevice(deviceId, "opening the tuner", Errno.NoSuchDevice, "Nothing can be received on it.");
        }

        if (Dead.Contains((deviceId, channel)))
        {
            throw DvbFailure.NoLock($"{deviceId} did not lock on channel {channel}.");
        }
    }

    public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
    {
        string deviceId = device.Id!;

        Take(deviceId, $"opened {deviceId} {tuning.PhysicalChannel}");

        if (Dead.Contains((deviceId, tuning.PhysicalChannel)))
        {
            Release(deviceId, $"closed {deviceId}");

            throw DvbFailure.NoLock($"{deviceId} did not lock on channel {tuning.PhysicalChannel}.");
        }

        return new Device(this, deviceId, tuning.PhysicalChannel);
    }

    public void LetGo(DeviceSettings device)
    {
        string deviceId = device.Id!;

        lock (gate)
        {
            if (busy.GetValueOrDefault(deviceId) > 0)
            {
                overlaps.Add($"let go of {deviceId} while {busy[deviceId]} operations held it");
            }

            happened.Add($"let go {deviceId}");
        }
    }

    public void Check(DeviceSettings device)
    {
        if (Broken.Contains(device.Id!))
        {
            throw DvbFailure.AtDevice(device.Id!, "opening the tuner", Errno.NoSuchDevice, "It is still broken.");
        }
    }

    private void Take(string deviceId, string what)
    {
        lock (gate)
        {
            int others = busy.GetValueOrDefault(deviceId);

            if (others > 0)
            {
                overlaps.Add($"{what} while {others} other operations held {deviceId}");
            }

            busy[deviceId] = others + 1;
            happened.Add(what);
        }
    }

    private void Release(string deviceId, string what)
    {
        lock (gate)
        {
            busy[deviceId] = busy.GetValueOrDefault(deviceId) - 1;
            happened.Add(what);
        }
    }

    private sealed class Probe(MeasuredHardware hardware, string deviceId, double[] signal) : IReceptionProbe
    {
        private int reads;
        private int closed;

        public SignalQuality Measure()
        {
            int read = reads++;

            hardware.WhileReading?.Invoke(deviceId, read);

            if (hardware.BreaksAtRead.TryGetValue(deviceId, out int breaksAt) && read >= breaksAt)
            {
                throw new IOException($"{deviceId} stopped answering while its signal was read.");
            }

            if (hardware.Unreadable.Contains((deviceId, read)))
            {
                throw DvbFailure.AtDevice(deviceId, "reading the signal statistics", Errno.NoSuchDevice, "No quality is reported.");
            }

            double decibels = signal[Math.Min(read, signal.Length - 1)];

            return double.IsNaN(decibels) ? Readings.WithoutLock() : Readings.Measured(decibels);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) is 0)
            {
                hardware.Release(deviceId, $"probe closed {deviceId}");
            }
        }
    }

    private sealed class Device(MeasuredHardware hardware, string deviceId, int channel) : ITunerDevice
    {
        private readonly FakeTunerDevice inner = new(channel);
        private int closed;

        public long Overflows => 0;

        public ISignalQualitySource? Quality => inner;

        public byte[] Read(int count, CancellationToken cancellationToken)
        {
            Thread.Sleep(1);
            cancellationToken.ThrowIfCancellationRequested();

            return inner.Read(count, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) is 0)
            {
                hardware.WhileClosing?.Invoke(deviceId);
                hardware.Release(deviceId, $"closed {deviceId}");
            }
        }
    }
}
