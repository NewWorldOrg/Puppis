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

    private readonly Dt295Bridge bridge;
    private readonly Dt295Stream stream;
    private readonly DevicePacing pacing;
    private readonly TimeSpan bytePatience;
    private readonly string channel;
    private readonly Action<Dt295Bridge>? keep;
    private readonly ILogger? logger;
    private readonly Lock gate = new();
    private bool closed;
    private bool faulted;

    private Dt295TunerDevice(
        Dt295Bridge bridge,
        Dt295Stream stream,
        DevicePacing pacing,
        TimeSpan bytePatience,
        string channel,
        Action<Dt295Bridge>? keep,
        ILogger? logger
    )
    {
        this.logger = logger;
        this.bridge = bridge;
        this.stream = stream;
        this.pacing = pacing;
        this.bytePatience = bytePatience;
        this.channel = channel;
        this.keep = keep;
    }

    public long Overflows => stream.Losses;

    public ISignalQualitySource? Quality => this;

    public bool IsOpen => !Volatile.Read(ref closed);

    /// <summary>
    /// The card slot of this tuner as a smart-card service, valid while the tuner is open.
    /// </summary>
    public ISmartCardService CardService() => bridge.CardService();

    /// <summary>
    /// Opens the device from power on, tunes it and starts the stream; closing gives the device back as it was found.
    /// </summary>
    public static Dt295TunerDevice Open(
        IUsbHost host,
        string path,
        DvbChannel channel,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger = null,
        string knownHandler = Dt295Startup.HandlerAtPowerOn
    ) => Start(Dt295Bridge.Open(host, path, pacing, logger, knownHandler), channel, pacing, bytePatience, logger, null);

    /// <summary>
    /// Tunes an open bridge and starts the stream. Closing stops the stream and hands the bridge to
    /// <paramref name="keep"/> at rest, unless the device failed, in which case the bridge is closed and given back;
    /// without <paramref name="keep"/> it is always closed. A channel that does not lock hands the bridge on the same
    /// way; any other failure closes it.
    /// </summary>
    public static Dt295TunerDevice Start(
        Dt295Bridge bridge,
        DvbChannel channel,
        DevicePacing pacing,
        TimeSpan bytePatience,
        ILogger? logger,
        Action<Dt295Bridge>? keep
    )
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            Tune(bridge, channel);
            Dt295Stream stream = bridge.StartStream();
            var device = new Dt295TunerDevice(bridge, stream, pacing, bytePatience, Describe(channel), keep, logger);
            device.Report(logger, Stopwatch.GetElapsedTime(started));

            return device;
        }
        catch (DvbDeviceException failed) when (failed.Failure is TuningFailure.NoLock)
        {
            LetGo(bridge, keep, faulted: false, logger);

            throw;
        }
        catch
        {
            LetGo(bridge, keep, faulted: true, logger);

            throw;
        }
    }

    /// <summary>
    /// Hands the bridge on at rest or closes it, whatever goes wrong on the way: a bridge is never left neither kept
    /// nor closed.
    /// </summary>
    internal static void LetGo(Dt295Bridge bridge, Action<Dt295Bridge>? keep, bool faulted, ILogger? logger)
    {
        try
        {
            bool stopped = bridge.StopStream();

            if (!faulted && stopped && keep is not null && bridge.Rest())
            {
                keep(bridge);

                return;
            }
        }
        catch (Exception failed) when (failed is not OutOfMemoryException)
        {
            logger?.LogWarning("{Path}: the tuner could not be put to rest, so it is closed: {Why}", bridge.Path, failed.GetType().Name);
        }

        try
        {
            bridge.Close();
        }
        catch (Exception failed) when (failed is not OutOfMemoryException)
        {
            logger?.LogWarning("{Path}: closing the tuner failed: {Why}", bridge.Path, failed.GetType().Name);
        }
    }

    internal static void Tune(Dt295Bridge bridge, DvbChannel channel)
    {
        Dt295Frontend frontend = bridge.Frontend;
        bool locked = channel switch
        {
            TerrestrialChannel terrestrial => frontend.TuneTerrestrial(DvbFrequency.TerrestrialHertz(terrestrial.PhysicalChannel)),
            BroadcastSatelliteChannel broadcast => frontend.TuneSatellite(
                DvbFrequency.BroadcastSatelliteKilohertz(broadcast.Slot),
                broadcast.TransportStreamId,
                bridge.Path
            ),
            CommunicationSatelliteChannel communication => frontend.TuneSatellite(
                DvbFrequency.CommunicationSatelliteKilohertz(communication.Slot),
                null,
                bridge.Path
            ),
            _ => throw DvbFailure.Refused($"{bridge.Path}: {channel} is not a channel this tuner receives."),
        };

        if (!locked)
        {
            throw DvbFailure.NoLock(
                $"{bridge.Path}: the demodulator did not synchronise on {Describe(channel)}. Nothing was received, so no bytes will follow."
            );
        }
    }

    public SignalQuality Measure()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);

            try
            {
                return QualityOf(bridge.Frontend);
            }
            catch (DvbDeviceException)
            {
                faulted = true;

                throw;
            }
        }
    }

    internal static SignalQuality QualityOf(Dt295Frontend frontend)
    {
        bool before = frontend.Locked();
        CarrierToNoise ratio = frontend.CarrierToNoise(before);
        bool after = frontend.Locked();

        return new SignalQuality(
            new LockWindow(Dt295Signal.StatusOf(before), Dt295Signal.StatusOf(after)),
            ratio,
            new PostViterbiErrors(SignalReading.NotImplementedByThisTuner, [])
        );
    }

    public byte[] Read(int count, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed))
        {
            throw Dt295Stream.Closing(cancellationToken);
        }

        byte[] taken;

        try
        {
            taken = stream.Take(count, bytePatience, cancellationToken);
        }
        catch (DvbDeviceException)
        {
            Volatile.Write(ref faulted, true);

            throw;
        }

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

        LetGo(bridge, keep, Volatile.Read(ref faulted) || stream.Faulted, logger);
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

                locked = bridge.Frontend.Locked();
            }
        }
        catch (DvbDeviceException)
        {
            Volatile.Write(ref faulted, true);

            return DvbFailure.Refused(
                $"{bridge.Path}: no transport stream arrived on {channel} within {waited}, and the demodulator would not say whether it is still locked."
            );
        }

        return locked
            ? DvbFailure.LockedWithoutData($"{bridge.Path}: the demodulator is locked on {channel} and no transport stream arrived within {waited}.")
            : DvbFailure.NoLock($"{bridge.Path}: no transport stream arrived on {channel} within {waited}, and the demodulator is no longer locked.");
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
            bridge.Path,
            channel,
            took.TotalMilliseconds,
            quality.HasLock,
            quality.CarrierToNoise.TryGetDecibels(out double decibels) ? $"{decibels:0.0} dB" : "not measured"
        );
    }
}
