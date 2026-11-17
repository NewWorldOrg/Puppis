using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The PIX-DT295 on USB, driven from its bridge's power-on state with nothing written to flash, and given back as
/// it was found when it closes. One device tunes one side, terrestrial or satellite, per opening.
/// </summary>
public sealed class Dt295TunerDevice : ITunerDevice, ISignalQualitySource
{
    public static readonly UsbModel Model = new(0x06B8, 0x103B);

    public const string ProductName = "PIXELA PIX-DT295";

    public const byte Interface = 0;

    private readonly Parts parts;
    private readonly Dt295Stream stream;
    private readonly DevicePacing pacing;
    private readonly TimeSpan bytePatience;
    private readonly string channel;
    private readonly Lock gate = new();
    private bool closed;

    private Dt295TunerDevice(Parts parts, Dt295Stream stream, DevicePacing pacing, TimeSpan bytePatience, string channel)
    {
        this.parts = parts;
        this.stream = stream;
        this.pacing = pacing;
        this.bytePatience = bytePatience;
        this.channel = channel;
    }

    public long Overflows => stream.Losses;

    public ISignalQualitySource? Quality => this;

    public bool IsOpen => !Volatile.Read(ref closed);

    /// <summary>
    /// The card slot of this tuner as a smart-card service, valid while the tuner is open.
    /// </summary>
    public ISmartCardService CardService() => parts.Slot.Service();

    public static Dt295TunerDevice Open(
        IUsbHost host,
        string path,
        DvbChannel channel,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger = null,
        string knownHandler = Dt295Startup.HandlerAtPowerOn
    )
    {
        UsbModel model = host.ModelAt(path);

        if (model != Model)
        {
            throw DvbFailure.Refused($"{path}: the device there is {model}, not the {Model} tuner this driver opened it as.");
        }

        long started = Stopwatch.GetTimestamp();
        Parts parts = new(new TurnTakingConnection(host.Open(path, Interface)), pacing, logger);

        try
        {
            parts.Start(knownHandler);
            parts.Tune(channel);
            Dt295Stream stream = parts.Stream();
            var device = new Dt295TunerDevice(parts, stream, pacing, bytePatience, Describe(channel));
            device.Report(logger, Stopwatch.GetElapsedTime(started));

            return device;
        }
        catch
        {
            parts.Close();

            throw;
        }
    }

    public SignalQuality Measure()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            bool before = parts.Frontend.Locked();
            CarrierToNoise ratio = parts.Frontend.CarrierToNoise(before);
            bool after = parts.Frontend.Locked();

            return new SignalQuality(
                new LockWindow(Dt295Signal.StatusOf(before), Dt295Signal.StatusOf(after)),
                ratio,
                new PostViterbiErrors(SignalReading.NotImplementedByThisTuner, [])
            );
        }
    }

    public byte[] Read(int count, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed))
        {
            throw Dt295Stream.Closing(cancellationToken);
        }

        byte[] taken = stream.Take(count, bytePatience, cancellationToken);

        return taken.Length > 0 ? taken : throw NothingArrived(cancellationToken);
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

        parts.Close();
    }

    private static string Describe(DvbChannel channel) =>
        channel switch
        {
            TerrestrialChannel terrestrial => $"terrestrial channel {terrestrial.PhysicalChannel}",
            BroadcastSatelliteChannel broadcast => $"BS slot {broadcast.Slot}",
            CommunicationSatelliteChannel communication => $"CS slot {communication.Slot}",
            _ => channel.ToString(),
        };

    private Exception NothingArrived(CancellationToken cancellationToken)
    {
        string waited = $"{bytePatience.TotalSeconds:0.#} seconds";
        bool locked;

        try
        {
            lock (gate)
            {
                if (closed)
                {
                    return Dt295Stream.Closing(cancellationToken);
                }

                locked = parts.Frontend.Locked();
            }
        }
        catch (DvbDeviceException)
        {
            return DvbFailure.Refused(
                $"{parts.Path}: no transport stream arrived on {channel} within {waited}, and the demodulator would not say whether it is still locked."
            );
        }

        return locked
            ? DvbFailure.LockedWithoutData($"{parts.Path}: the demodulator is locked on {channel} and no transport stream arrived within {waited}.")
            : DvbFailure.NoLock($"{parts.Path}: no transport stream arrived on {channel} within {waited}, and the demodulator is no longer locked.");
    }

    private void Report(ILogger? logger, TimeSpan took)
    {
        if (logger is null)
        {
            return;
        }

        SignalQuality quality = Measure();
        logger.LogInformation(
            "{Path}: started, tuned to {Channel} and streaming after {Milliseconds:0} ms; locked {Locked}, C/N {Ratio}.",
            parts.Path,
            channel,
            took.TotalMilliseconds,
            quality.HasLock,
            quality.CarrierToNoise.TryGetDecibels(out double decibels) ? $"{decibels:0.0} dB" : "not measured"
        );
    }

    /// <summary>
    /// What an opening has put in place, closed in the reverse order whatever stage it reached.
    /// </summary>
    private sealed class Parts
    {
        private readonly TurnTakingConnection usb;
        private readonly DevicePacing pacing;
        private readonly BridgeMemory memory;
        private readonly BridgeWindow window;
        private readonly Dt295Capture capture;
        private readonly Fr30Program program = CopyProgram.Build();
        private readonly ILogger? logger;
        private bool started;
        private bool capturing;
        private EndpointState? endpoint;
        private IUsbBulkReader? reader;
        private Dt295Stream? stream;

        public Parts(TurnTakingConnection usb, DevicePacing pacing, ILogger? logger)
        {
            this.usb = usb;
            this.pacing = pacing;
            this.logger = logger;
            memory = new BridgeMemory(usb);
            window = new BridgeWindow(memory);
            capture = new Dt295Capture(window, memory);
            Frontend = new Dt295Frontend(new Dt295Chips(new BridgeI2c(memory)), pacing);
            Slot = new Dt295CardSlot(new CardUart(new BridgeMemory(usb), pacing));
        }

        public string Path => usb.Path;

        public Dt295Frontend Frontend { get; }

        public Dt295CardSlot Slot { get; }

        public void Start(string knownHandler)
        {
            Dt295Startup.RequirePowerOnState(memory);
            Dt295Startup.RequireTheKnownHandler(memory, knownHandler);
            started = true;
            Dt295Startup.ResetTheDemodulator(memory, pacing);
            Dt295Startup.StartClocks(memory);
            Dt295Startup.StartI2cMaster(memory);
            Frontend.Identify(usb.Path);
            Frontend.Initialise();
            CopyProgram.Install(memory, program, () => Dt295Stream.Instruct(usb, memory, 0, CopyCommand.Answer, 0, 0));
        }

        public void Tune(DvbChannel channel)
        {
            bool locked = channel switch
            {
                TerrestrialChannel terrestrial => Frontend.TuneTerrestrial(DvbFrequency.TerrestrialHertz(terrestrial.PhysicalChannel)),
                BroadcastSatelliteChannel broadcast => Frontend.TuneSatellite(
                    DvbFrequency.BroadcastSatelliteKilohertz(broadcast.Slot),
                    broadcast.TransportStreamId,
                    usb.Path
                ),
                CommunicationSatelliteChannel communication => Frontend.TuneSatellite(
                    DvbFrequency.CommunicationSatelliteKilohertz(communication.Slot),
                    null,
                    usb.Path
                ),
                _ => throw DvbFailure.Refused($"{usb.Path}: {channel} is not a channel this tuner receives."),
            };

            if (!locked)
            {
                throw DvbFailure.NoLock(
                    $"{usb.Path}: the demodulator did not synchronise on {Describe(channel)}. Nothing was received, so no bytes will follow."
                );
            }
        }

        public Dt295Stream Stream()
        {
            capture.Prepare();
            endpoint = capture.KeepTheEndpoint();
            capture.ReadyTheEndpoint(endpoint);
            reader = usb.ReadBulk(Dt295Capture.Endpoint, Dt295Capture.TransferBytes, Dt295Capture.TransfersInFlight);
            capturing = true;
            capture.Start();
            stream = new Dt295Stream(usb, new BridgeMemory(usb), capture.Writer, reader, pacing);
            stream.Start();

            return stream;
        }

        /// <summary>
        /// Gives the bridge back step by step, carrying on past a step that fails and saying which one did.
        /// </summary>
        public void Close()
        {
            Quietly("stopping the copying", () => stream?.Dispose());

            if (capturing)
            {
                Quietly("stopping the capture", capture.Stop);
            }

            Quietly("cancelling the bulk reads", () => reader?.Dispose());

            if (endpoint is not null)
            {
                Quietly("putting the endpoint back", () => capture.RestoreTheEndpoint(endpoint));
            }

            if (started)
            {
                Quietly("taking the hook out", () => CopyProgram.UnhookIfOurs(memory));
                Quietly("switching the card off", Slot.Close);
                Quietly("putting the window back", window.PutBack);
            }

            usb.Dispose();
        }

        private bool Quietly(string step, Action action)
        {
            try
            {
                action();

                return true;
            }
            catch (Exception failed) when (failed is DvbDeviceException or InvalidOperationException)
            {
                logger?.LogWarning("{Path}: {Step} failed while the tuner closed: {Why}", usb.Path, step, failed.GetType().Name);

                return false;
            }
        }
    }
}
