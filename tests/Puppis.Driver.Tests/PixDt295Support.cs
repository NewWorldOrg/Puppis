using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

internal sealed class FakeDt295Host(FakeDt295 device) : IUsbHost
{
    public List<UsbDeviceNode> Nodes { get; } = [];

    public int Opened { get; private set; }

    public IReadOnlyList<UsbDeviceNode> List() => Nodes;

    public UsbModel ModelAt(string path) =>
        Nodes.FirstOrDefault(node => node.Path == path)?.Model ?? Dt295TunerDevice.Model;

    public IUsbConnection Open(string path, byte interfaceNumber)
    {
        Opened++;

        return device;
    }
}

public sealed class SteppedClock(DateTimeOffset start) : TimeProvider
{
    private long ticks;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

internal static class PixDt295Support
{
    public static (DevicePacing Pacing, SteppedClock Clock) Pacing(FakeDt295 device)
    {
        var clock = new SteppedClock(UsbTestSupport.Start);

        return (
            new DevicePacing(
                clock,
                span =>
                {
                    lock (device.Log)
                    {
                        device.Log.Add($"wait {span.TotalMilliseconds:0}");
                    }

                    clock.Advance(span);
                }
            ),
            clock
        );
    }

    public static Dt295Chips Chips(FakeDt295 device) => new(new BridgeI2c(new BridgeMemory(new TurnTakingConnection(device))));

    public static IReadOnlyList<string> Expected(string steps) =>
    [
        .. steps
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(Expand),
    ];

    public static IReadOnlyList<string> Written(FakeDt295 device) =>
        [.. device.Log.Select(line => line.Contains(" r[", StringComparison.Ordinal) ? line[..line.IndexOf(" r[", StringComparison.Ordinal)] : line)];

    private static IEnumerable<string> Expand(string step)
    {
        string[] parts = step.Split(' ');
        string rest = string.Concat(parts[1..]);

        return parts[0] switch
        {
            "wait" => [step],
            "T" => [$"20 w[{rest}]"],
            "S" => [$"22 w[{rest}]"],
            "RT" => [$"20 w[FEC0{rest}]"],
            "RS" => [$"22 w[FEC0{rest}]"],
            "RTM" => [$"20 w[FEC0FB{parts[1]}]", "20 w[FEC1]", $"20 w[FEC0{parts[1]}{parts[2]}]"],
            "TM" => [$"20 w[{parts[1]}]", $"20 w[{parts[1]}{parts[2]}]"],
            "SM" => [$"22 w[{parts[1]}]", $"22 w[{parts[1]}{parts[2]}]"],
            "TR" => [$"20 w[{parts[1]}]"],
            "SR" => [$"22 w[{parts[1]}]"],
            "RSR" => ["22 w[FEC1]"],
            _ => throw new ArgumentException($"'{step}' is not a step."),
        };
    }
}
