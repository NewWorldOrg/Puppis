using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Puppis.Driver.Transport;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

/// <summary>
/// The CECH-ZD1 terrestrial tuner on USB, driven from its bridge's power-on state with nothing written to flash.
/// </summary>
public sealed class CechZd1TunerDevice : ITunerDevice, ISignalQualitySource
{
    public static readonly UsbModel Model = new(0x054C, 0x04B2);

    public const string ProductName = "Sony CECH-ZD1";

    public const byte Interface = 0;

    public static readonly TimeSpan TransferPatience = TimeSpan.FromMilliseconds(300);

    private readonly CechZd1Standby.KeptTuner kept;
    private readonly IUsbConnection usb;
    private readonly CechZd1Frontend frontend;
    private readonly TransportCapture capture;
    private readonly IUsbBulkReader reader;
    private readonly DevicePacing pacing;
    private readonly TimeSpan bytePatience;
    private readonly CechZd1Standby? standby;
    private readonly CaptureUnpacker unpacker = new();
    private readonly List<byte> packets = [];
    private readonly byte[] transfer = new byte[TransportCapture.TransferBytes];
    private readonly Lock gate = new();
    private ILogger? logger;
    private long opened;
    private long hertz;
    private bool firstBytesReported;
    private bool closed;

    private CechZd1TunerDevice(
        CechZd1Standby.KeptTuner kept,
        IUsbBulkReader reader,
        DevicePacing pacing,
        TimeSpan bytePatience,
        CechZd1Standby? standby
    )
    {
        this.kept = kept;
        usb = kept.Usb;
        frontend = kept.Frontend;
        capture = kept.Capture;
        this.reader = reader;
        this.pacing = pacing;
        this.bytePatience = bytePatience;
        this.standby = standby;
    }

    public long Overflows => unpacker.Realignments;

    public ISignalQualitySource? Quality => this;

    public static void Recognise(IUsbHost host, string path)
    {
        UsbModel model = host.ModelAt(path);

        if (model != Model)
        {
            throw DvbFailure.Refused(
                $"{path}: the device there is {model}, not the {Model} tuner this backend drives."
            );
        }
    }

    public static CechZd1TunerDevice Open(
        IUsbHost host,
        string path,
        long hertz,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger = null,
        CechZd1Standby? standby = null
    )
    {
        Recognise(host, path);

        if (standby?.Take(path) is { } kept)
        {
            try
            {
                return WakeAndTune(kept, hertz, pacing, bytePatience, logger, standby);
            }
            catch (DvbDeviceException fault)
            {
                logger?.LogWarning(
                    fault,
                    "{Path}: the tuner kept open since the last session did not lock, so it is started afresh.",
                    path
                );
            }
        }

        return StartAndTune(host, path, hertz, pacing, bytePatience, logger, standby);
    }

    public static void OpenAndClose(IUsbHost host, string path, CechZd1Standby? standby = null)
    {
        Recognise(host, path);

        if (standby?.Holds(path) is true)
        {
            standby.Check(path);

            return;
        }

        host.Open(path, Interface).Dispose();
    }

    private static CechZd1TunerDevice StartAndTune(
        IUsbHost host,
        string path,
        long hertz,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger,
        CechZd1Standby? standby
    )
    {
        long started = Stopwatch.GetTimestamp();
        IUsbConnection usb = host.Open(path, Interface);
        CechZd1Frontend? frontend = null;

        try
        {
            var memory = new BridgeMemory(usb);
            var stages = new OpeningStages(started, memory);
            BridgeStartup.Start(memory, pacing);
            stages.Mark("bridge");
            var i2c = new BridgeI2c(memory);
            frontend = new CechZd1Frontend(new Dib8000Bus(i2c), new Dib0090Bus(i2c), pacing, path);
            frontend.Initialise();
            stages.Mark("frontend");
            var kept = new CechZd1Standby.KeptTuner(path, usb, memory, frontend, new TransportCapture(memory));

            return Tune(kept, hertz, pacing, bytePatience, logger, standby, stages, "started");
        }
        catch
        {
            Quietly(() => frontend?.Sleep());
            Quietly(() => frontend?.Demodulator.LedGreen());
            usb.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Tunes a tuner kept from the last session. Whatever goes wrong, the kept tuner is closed, so that the device is
    /// started from its bridge again rather than kept in a state that does not lock.
    /// </summary>
    private static CechZd1TunerDevice WakeAndTune(
        CechZd1Standby.KeptTuner kept,
        long hertz,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger,
        CechZd1Standby standby
    )
    {
        var stages = new OpeningStages(Stopwatch.GetTimestamp(), kept.Memory);

        try
        {
            return Tune(kept, hertz, pacing, bytePatience, logger, standby, stages, "woken");
        }
        catch
        {
            Quietly(kept.Frontend.Sleep);
            Quietly(() => kept.Frontend.Demodulator.LedGreen());
            kept.Usb.Dispose();

            throw;
        }
    }

    private static CechZd1TunerDevice Tune(
        CechZd1Standby.KeptTuner kept,
        long hertz,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger,
        CechZd1Standby? standby,
        OpeningStages stages,
        string how
    )
    {
        kept.Capture.Prepare();
        stages.Mark("capture");

        if (kept.Frontend.Tune(hertz) is SearchOutcome.Failed)
        {
            throw DvbFailure.NoLock(
                $"{kept.Path}: the demodulator did not synchronise on {hertz / 1000} kHz. Nothing was received, so no bytes will follow."
            );
        }

        stages.Mark("tune");
        IUsbBulkReader reader = kept.Usb.ReadBulk(
            TransportCapture.Endpoint,
            TransportCapture.TransferBytes,
            TransportCapture.TransfersInFlight
        );
        kept.Capture.Start();
        stages.Mark("start");
        var device = new CechZd1TunerDevice(kept, reader, pacing, bytePatience, standby);
        device.logger = logger;
        device.opened = stages.Started;
        device.hertz = hertz;
        device.Report(logger, hertz, stages, how);

        return device;
    }

    /// <summary>
    /// Puts the frontend to sleep and keeps it for the next session, or closes the device when it will not sleep.
    /// </summary>
    private static void Rest(CechZd1Standby.KeptTuner kept, CechZd1Standby? standby)
    {
        bool asleep = Quietly(() => kept.Frontend.Demodulator.LedGreen()) & Quietly(kept.Frontend.Sleep);

        if (asleep && standby is not null)
        {
            standby.Keep(kept);
        }
        else
        {
            kept.Usb.Dispose();
        }
    }

    public SignalQuality Measure()
    {
        SignalRegisters registers;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            registers = frontend.ReadSignal();
        }

        return new SignalQuality(
            new LockWindow(CechZd1Signal.StatusOf(registers.StatusBefore), CechZd1Signal.StatusOf(registers.StatusAfter)),
            CechZd1Signal.CarrierToNoiseFrom(registers),
            new PostViterbiErrors(SignalReading.NotImplementedByThisTuner, [])
        );
    }

    public byte[] Read(int count, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = pacing.Now + bytePatience;

        while (packets.Count < TsPacketReader.PacketLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int arrived = reader.Take(transfer, TransferPatience);

            if (arrived > 0)
            {
                unpacker.Unpack(transfer.AsSpan(0, arrived), packets);
                ReportFirstBytes(arrived);
            }
            else if (pacing.Now >= deadline)
            {
                throw NothingArrived();
            }
        }

        int whole = Math.Max(count / TsPacketReader.PacketLength, 1) * TsPacketReader.PacketLength;
        int taking = Math.Min(packets.Count / TsPacketReader.PacketLength * TsPacketReader.PacketLength, whole);
        byte[] delivered = new byte[taking];
        packets.CopyTo(0, delivered, 0, taking);
        packets.RemoveRange(0, taking);

        return delivered;
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
            long began = Stopwatch.GetTimestamp();
            bool stopped = Quietly(capture.Stop) & Quietly(reader.Dispose);

            if (stopped)
            {
                Rest(kept, standby);
            }
            else
            {
                usb.Dispose();
            }

            logger?.LogInformation(
                "{Path}: closing took {Milliseconds:0} ms; the tuner is {State}.",
                usb.Path,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds,
                standby?.Holds(usb.Path) is true ? "kept asleep for the next session" : "closed"
            );
        }
    }

    private DvbDeviceException NothingArrived()
    {
        string waited = $"{bytePatience.TotalSeconds:0.#} seconds";
        SignalRegisters registers;

        try
        {
            lock (gate)
            {
                registers = frontend.ReadSignal();
            }
        }
        catch (DvbDeviceException)
        {
            return DvbFailure.Refused(
                $"{usb.Path}: no transport stream arrived on {hertz / 1000} kHz within {waited}, and the demodulator would not say whether it is still locked."
            );
        }

        return CechZd1Signal.LockedWithData(registers.StatusAfter)
            ? DvbFailure.LockedWithoutData(
                $"{usb.Path}: the demodulator is locked on {hertz / 1000} kHz and no transport stream arrived within {waited}."
            )
            : DvbFailure.NoLock(
                $"{usb.Path}: no transport stream arrived on {hertz / 1000} kHz within {waited}, and the demodulator is no longer locked (0x{registers.StatusAfter:X4})."
            );
    }

    private void ReportFirstBytes(int arrived)
    {
        if (firstBytesReported)
        {
            return;
        }

        firstBytesReported = true;
        logger?.LogInformation(
            "{Path}: the first bulk read, {Bytes} bytes, arrived {Seconds:0.000} s after the tuner was opened.",
            usb.Path,
            arrived,
            Stopwatch.GetElapsedTime(opened).TotalSeconds
        );
    }

    private void Report(ILogger? logger, long hertz, OpeningStages stages, string how)
    {
        if (logger is null)
        {
            return;
        }

        logger.LogInformation("{Path}: {How} and tuned in {Stages}.", usb.Path, how, stages);

        SignalRegisters registers = frontend.ReadSignal();
        double? decibels = CechZd1Signal.Decibels(
            CechZd1Signal.Expand(registers.Signal),
            CechZd1Signal.Expand(registers.Noise)
        );
        logger.LogInformation(
            "{Path} locked on {Kilohertz} kHz: status 0x{Status:X4}, signal {Strength:0.0} dBm, C/N {CarrierToNoise:0.0} dB.",
            usb.Path,
            hertz / 1000,
            registers.StatusAfter,
            CechZd1Signal.StrengthDbm(registers.Strength),
            decibels
        );
    }

    private static bool Quietly(Action action)
    {
        try
        {
            action();

            return true;
        }
        catch (DvbDeviceException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class OpeningStages(long started, BridgeMemory memory)
{
    private readonly List<string> marks = [];
    private long last = started;
    private long requests = memory.Requests;

    public long Started { get; } = started;

    public void Mark(string stage)
    {
        long now = Stopwatch.GetTimestamp();
        long sent = memory.Requests;
        marks.Add($"{stage} {Stopwatch.GetElapsedTime(last, now).TotalMilliseconds:0} ms/{sent - requests} requests");
        last = now;
        requests = sent;
    }

    public override string ToString() =>
        $"{Stopwatch.GetElapsedTime(Started).TotalMilliseconds:0} ms in all ({string.Join(", ", marks)})";
}
