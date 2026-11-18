using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.CechZd1;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tuning;

public interface ITunerDevice : IDisposable
{
    long Overflows { get; }

    ISignalQualitySource? Quality => null;

    byte[] Read(int count, CancellationToken cancellationToken);
}

public interface ITunerDeviceFactory
{
    ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold);
}

public interface ITunerDeviceCheck
{
    void Check(DeviceSettings device);

    /// <summary>
    /// Closes whatever is kept open for <paramref name="device"/> between its sessions, when anything is.
    /// </summary>
    void LetGo(DeviceSettings device) { }
}

public sealed class TunerDeviceFactory : ITunerDeviceFactory, ITunerDeviceCheck, IDisposable
{
    private readonly Dictionary<string, CechZd1Standby> standbys = new(StringComparer.Ordinal);
    private readonly Lock standing = new();
    private bool disposed;
    private readonly Dt295Keeper keeper = new();
    private readonly Lock opening = new();
    private readonly TunerBackend backend;
    private readonly TimeProvider time;
    private readonly Lazy<IDvbSystemCalls> systemCalls;
    private readonly Lazy<IUsbHost> usbHost;
    private readonly DevicePacing pacing;
    private readonly IDescramblerFactory descramblers;
    private readonly ILogger? logger;
    private readonly DvbTunerSettings settings;
    private readonly string dt295Handler;

    public TunerDeviceFactory(
        DriverConfiguration configuration,
        TimeProvider time,
        IDescramblerFactory descramblers,
        ILogger<TunerDeviceFactory>? logger = null
    )
        : this(
            configuration,
            time,
            new Lazy<IDvbSystemCalls>(() => new LinuxDvbSystemCalls()),
            new Lazy<IUsbHost>(() => new LinuxUsbHost()),
            DevicePacing.RealTime(time),
            descramblers,
            logger
        )
    { }

    public TunerDeviceFactory(DriverConfiguration configuration, TimeProvider time)
        : this(configuration, time, NoDescrambling.Instance) { }

    private TunerDeviceFactory(
        DriverConfiguration configuration,
        TimeProvider time,
        Lazy<IDvbSystemCalls> systemCalls,
        Lazy<IUsbHost> usbHost,
        DevicePacing pacing,
        IDescramblerFactory descramblers,
        ILogger? logger = null,
        string dt295Handler = Dt295Startup.HandlerAtPowerOn
    )
    {
        this.dt295Handler = dt295Handler;
        this.usbHost = usbHost;
        this.pacing = pacing;
        this.logger = logger;
        backend = configuration.Tuner?.Backend ?? TunerBackend.Unspecified;
        this.time = time;
        this.systemCalls = systemCalls;
        this.descramblers = descramblers;
        settings = DvbTunerSettings.Default with
        {
            DemuxBufferBytes =
                configuration.Tuner?.DemuxBufferBytes ?? TunerSettings.DefaultDemuxBufferBytes,
        };
    }

    public static TunerDeviceFactory Using(
        DriverConfiguration configuration,
        TimeProvider time,
        IDvbSystemCalls systemCalls,
        IDescramblerFactory? descramblers = null
    ) =>
        new(
            configuration,
            time,
            new Lazy<IDvbSystemCalls>(() => systemCalls),
            new Lazy<IUsbHost>(() => new LinuxUsbHost()),
            DevicePacing.RealTime(time),
            descramblers ?? NoDescrambling.Instance
        );

    /// <param name="dt295Handler">The digest a PIX-DT295's request handling must have, for a simulated one.</param>
    public static TunerDeviceFactory Using(
        DriverConfiguration configuration,
        IUsbHost usbHost,
        DevicePacing pacing,
        IDescramblerFactory? descramblers = null,
        string dt295Handler = Dt295Startup.HandlerAtPowerOn,
        ILogger? logger = null
    ) =>
        new(
            configuration,
            TimeProvider.System,
            new Lazy<IDvbSystemCalls>(() => new LinuxDvbSystemCalls()),
            new Lazy<IUsbHost>(() => usbHost),
            pacing,
            descramblers ?? NoDescrambling.Instance,
            logger,
            dt295Handler
        );

    public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold) =>
        backend switch
        {
            TunerBackend.Fake => Synthetic(tune?.ToLegacyRequest() ?? tuning),
            TunerBackend.Dvb => OpenDvb(device, tuning, tune, hold),
            TunerBackend.Usb => OpenUsb(device, tuning, tune, hold),
            _ => throw BackendNeverEstablished(),
        };

    public void Check(DeviceSettings device)
    {
        switch (backend)
        {
            case TunerBackend.Fake:
                return;
            case TunerBackend.Dvb:
                DvbTunerDevice.OpenAndClose(systemCalls.Value, PathsOf(device));

                return;
            case TunerBackend.Usb:
                CheckUsb(device, UsbPathOf(device));

                return;
            default:
                throw BackendNeverEstablished();
        }
    }

    private static InvalidOperationException BackendNeverEstablished() =>
        new(
            "The tuner backend was never established; the configuration should have been rejected."
        );

    private static DvbDevicePaths PathsOf(DeviceSettings device) =>
        DvbDevicePaths.TryDerive(device.DevicePath, out DvbDevicePaths? paths, out string? problem)
            ? paths
            : throw DvbFailure.Refused($"devices['{device.Id}']: {problem}");

    private string UsbPathOf(DeviceSettings device) =>
        UsbTunerDetector.NodeFor(usbHost.Value, device.Id, device.DevicePath)
        ?? throw DvbFailure.Refused($"devices['{device.Id}']: a usb device needs a devicePath.");

    public void LetGo(DeviceSettings device)
    {
        if (device.Id is not { } deviceId)
        {
            return;
        }

        keeper.LetGo(deviceId);

        CechZd1Standby? standby;

        lock (standing)
        {
            standbys.TryGetValue(deviceId, out standby);
        }

        standby?.Release();
    }

    /// <summary>
    /// Takes in what a presence check found for <paramref name="device"/>: a tuner gone is let go of, and a PIX-DT295
    /// found is written down with its slot, looked at once if nobody has looked since it was plugged in. Nothing here
    /// throws; a device that cannot be asked counts as not yet looked at.
    /// </summary>
    public void Looked(DeviceSettings device, bool present)
    {
        if (device.Id is not { } deviceId)
        {
            return;
        }

        SlotCards? slots = descramblers as SlotCards;

        if (!present)
        {
            try
            {
                slots?.Absent(deviceId);
                keeper.LetGo(deviceId);
            }
            catch (Exception failed) when (failed is not OutOfMemoryException)
            {
                logger?.LogWarning("devices['{DeviceId}']: letting go of the tuner that is gone failed: {Why}", deviceId, failed.GetType().Name);
            }

            return;
        }

        if (slots is null || backend is not TunerBackend.Usb)
        {
            return;
        }

        try
        {
            string path = UsbPathOf(device);

            if (usbHost.Value.ModelAt(path) != Dt295TunerDevice.Model)
            {
                slots.Absent(deviceId);

                return;
            }

            slots.Present(deviceId, keeper.Holds(deviceId) || slots.Knows(deviceId) ? null : PeekAtTheSlot(path));
        }
        catch (Exception failed) when (failed is DvbDeviceException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("devices['{DeviceId}']: the tuner could not be looked at: {Why}", deviceId, failed.Message);
        }
    }

    /// <summary>
    /// Whether the slot of an idle PIX-DT295 nobody holds open holds a card: one register read, which the bridge
    /// answers as it powers on, or null when the device would not be opened.
    /// </summary>
    private bool? PeekAtTheSlot(string path)
    {
        lock (opening)
        {
            try
            {
                using IUsbConnection usb = usbHost.Value.Open(path, Dt295TunerDevice.Interface);

                return (new BridgeMemory(usb).Read(CardUart.Status) & CardUart.CardInserted) is not 0;
            }
            catch (DvbDeviceException)
            {
                return null;
            }
        }
    }

    private void CheckUsb(DeviceSettings device, string path)
    {
        if (usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model)
        {
            CheckDt295(device, path);

            return;
        }

        CechZd1TunerDevice.OpenAndClose(usbHost.Value, path, StandbyFor(device, path));
    }

    private ITunerDevice OpenUsb(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
    {
        string path = UsbPathOf(device);

        return usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model
            ? OpenDt295(device, path, DvbTuneRequest.Resolve(tune, tuning), hold)
            : OpenCechZd1(device, path, tuning, tune, hold);
    }

    /// <summary>
    /// A kept PIX-DT295 is asked whether it is still as it was left, without being opened again; any other is opened
    /// afresh, from its bridge's start-up to the program hooked in, and then kept for the next session.
    /// </summary>
    private void CheckDt295(DeviceSettings device, string path)
    {
        string deviceId = device.Id ?? path;

        lock (opening)
        {
            if (!keeper.Holds(deviceId))
            {
                keeper.Keep(deviceId, OpenAfresh(deviceId, path));

                return;
            }

            Dt295Bridge kept =
                keeper.Take(deviceId, path)
                ?? throw new DvbDeviceException(
                    $"devices['{deviceId}']: the tuner kept open is no longer as it was left, so it has been closed.",
                    TuningFailure.DeviceUnusable
                );
            keeper.Keep(deviceId, kept);
        }
    }

    /// <summary>
    /// Opens a PIX-DT295, or takes the one kept open since its last session, which then only has to be tuned. It is
    /// read through the card a lender holds where there is one and through the card in the tuner's own slot where
    /// there is not, asked for while the tuner tunes.
    /// </summary>
    private ITunerDevice OpenDt295(DeviceSettings device, string path, DvbChannel channel, HeadHold hold)
    {
        RequireTheDeclaredKind(device, channel);
        string deviceId = device.Id ?? path;
        Dt295Bridge bridge;

        lock (opening)
        {
            bridge = keeper.Take(deviceId, path) ?? OpenAfresh(deviceId, path);
        }

        Dt295TunerDevice Start() =>
            Dt295TunerDevice.Start(
                bridge,
                channel,
                pacing,
                settings.BytePatience,
                logger,
                rested => keeper.Keep(deviceId, rested)
            );

        if (descramblers.Unscrambles && descramblers is not SlotCards)
        {
            return ThroughTheCard(Start, hold, descramblers);
        }

        IDescramblerFactory slot;

        try
        {
            int session = bridge.Slot.BeginSession();
            ISmartCardService Cards() => bridge.Slot.Service(session);

            slot = descramblers is SlotCards slots
                ? slots.Attach(deviceId, bridge, Cards)
                : Descramblers.Over(Cards, logger, time);
        }
        catch
        {
            bridge.Close();

            throw;
        }

        return ThroughTheCard(Start, hold, slot);
    }

    /// <summary>
    /// Opens a PIX-DT295 from power on. It may be kept again afterwards unless it is let go of while it opens.
    /// </summary>
    private Dt295Bridge OpenAfresh(string deviceId, string path)
    {
        keeper.Admit(deviceId);

        return Dt295Bridge.Open(usbHost.Value, path, pacing, logger, dt295Handler);
    }

    private static void RequireTheDeclaredKind(DeviceSettings device, DvbChannel channel)
    {
        bool satellite = channel is not TerrestrialChannel;

        if (satellite && device.Kind is DeviceKind.Terrestrial)
        {
            throw DvbFailure.Refused(
                $"devices['{device.Id}']: this tuner is declared terrestrial, and this tune asks for a satellite."
            );
        }

        if (!satellite && device.Kind is DeviceKind.Satellite)
        {
            throw DvbFailure.Refused(
                $"devices['{device.Id}']: this tuner is declared satellite, and this tune asks for terrestrial broadcasting."
            );
        }
    }

    private ITunerDevice OpenCechZd1(DeviceSettings device, string path, TuningRequest tuning, TuneParams? tune, HeadHold hold)
    {
        if (DvbTuneRequest.Resolve(tune, tuning) is not TerrestrialChannel channel)
        {
            throw DvbFailure.Refused(
                $"devices['{device.Id}']: the usb tuner receives terrestrial broadcasting only, and this tune asks for a satellite."
            );
        }

        return ThroughTheCard(
            () =>
                CechZd1TunerDevice.Open(
                    usbHost.Value,
                    path,
                    DvbFrequency.TerrestrialHertz(channel.PhysicalChannel),
                    pacing,
                    settings.BytePatience,
                    logger,
                    StandbyFor(device, path)
                ),
            hold,
            Shared
        );
    }

    private CechZd1Standby StandbyFor(DeviceSettings device, string path)
    {
        string deviceId = device.Id ?? path;

        lock (standing)
        {
            if (!standbys.TryGetValue(deviceId, out CechZd1Standby? standby))
            {
                standby = new CechZd1Standby();

                if (disposed)
                {
                    standby.Dispose();
                }
                else
                {
                    standbys[deviceId] = standby;
                }
            }

            return standby;
        }
    }

    public void Dispose()
    {
        CechZd1Standby[] closing;

        lock (standing)
        {
            disposed = true;
            closing = [.. standbys.Values];
            standbys.Clear();
        }

        foreach (CechZd1Standby standby in closing)
        {
            standby.Dispose();
        }

        keeper.Dispose();
    }

    private static ITunerDevice Synthetic(TuningRequest tuning) =>
        new FakeTunerDevice(tuning.PhysicalChannel, tuning.ServiceId);

    private ITunerDevice OpenDvb(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
    {
        DvbDevicePaths paths = PathsOf(device);

        return ThroughTheCard(
            () =>
                DvbTunerDevice.Open(
                    systemCalls.Value,
                    time,
                    paths,
                    DvbTuneRequest.Resolve(tune, tuning),
                    LnbPower.For(device.Kind, device.LnbPower),
                    settings with { ReadsFromTheLock = HeadHolds.ReadsFromTheLock(hold) },
                    CancellationToken.None
                ),
            hold,
            Shared
        );
    }

    /// <summary>
    /// Tunes the device while the card is asked for, so that the card's answer to the initial setting is not added to
    /// the time the frontend takes to lock. A card that answers after the tuning has failed is let go of.
    /// </summary>
    private ITunerDevice ThroughTheCard(Func<ITunerDevice> open, HeadHold hold, IDescramblerFactory cards)
    {
        long started = Stopwatch.GetTimestamp();
        Task<IDescrambler?>? asking = cards.Unscrambles ? Task.Run(() => cards.Open(hold)) : null;
        ITunerDevice opened;

        try
        {
            opened = open();
        }
        catch
        {
            LetGoOnceAnswered(asking);

            throw;
        }

        if (asking is null)
        {
            return opened;
        }

        TimeSpan tuned = Stopwatch.GetElapsedTime(started);
        IDescrambler? descrambler;

        try
        {
            descrambler = asking.GetAwaiter().GetResult();
        }
        catch
        {
            opened.Dispose();

            throw;
        }

        logger?.LogInformation(
            "The tuner was open after {Tuned:0.000} s and the card was in hand after {Card:0.000} s.",
            tuned.TotalSeconds,
            Stopwatch.GetElapsedTime(started).TotalSeconds
        );

        return Unscrambling(opened, descrambler, cards, hold);
    }

    private static void LetGoOnceAnswered(Task<IDescrambler?>? asking) =>
        asking?.ContinueWith(
            static answered =>
            {
                if (answered.IsCompletedSuccessfully)
                {
                    answered.Result?.Dispose();
                }
                else
                {
                    _ = answered.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>
    /// The cards every tuner of this driver may be read through. The cards in tuners' own slots are not among them:
    /// each serves only the tuner it sits in.
    /// </summary>
    private IDescramblerFactory Shared => descramblers is SlotCards ? NoDescrambling.Instance : descramblers;

    private ITunerDevice Unscrambling(ITunerDevice opened, IDescrambler? descrambler, IDescramblerFactory cards, HeadHold hold)
    {
        if (descrambler is null)
        {
            logger?.LogWarning(
                "No card answered the reader as this tuner opened, so what it carries stays scrambled until one does; the card is asked for again while the tuner is read."
            );
        }

        try
        {
            return new DescramblingTunerDevice(opened, descrambler, cards, time, logger, hold);
        }
        catch
        {
            descrambler?.Dispose();

            throw;
        }
    }
}
