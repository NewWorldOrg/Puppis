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

    ITunerDevice Create(
        DeviceSettings device,
        TuningRequest tuning,
        TuneParams? tune,
        HeadHold hold,
        SessionPurpose? purpose
    ) => Create(device, tuning, tune, hold);
}

public interface ITunerDeviceCheck
{
    void Check(DeviceSettings device);

    void LetGo(DeviceSettings device) { }

    bool IsKeptOpen(DeviceSettings device) => false;
}

public sealed class TunerDeviceFactory : ITunerDeviceFactory, ITunerDeviceCheck, IReceptionProbeFactory, IDisposable
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
        IDescramblerFactory? descramblers = null,
        ILogger? logger = null
    ) =>
        new(
            configuration,
            time,
            new Lazy<IDvbSystemCalls>(() => systemCalls),
            new Lazy<IUsbHost>(() => new LinuxUsbHost()),
            DevicePacing.RealTime(time),
            descramblers ?? NoDescrambling.Instance,
            logger
        );

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
        Create(device, tuning, tune, hold, null);

    public ITunerDevice Create(
        DeviceSettings device,
        TuningRequest tuning,
        TuneParams? tune,
        HeadHold hold,
        SessionPurpose? purpose
    ) =>
        backend switch
        {
            TunerBackend.Fake => Synthetic(tune?.ToLegacyRequest() ?? tuning),
            TunerBackend.Dvb => OpenDvb(device, tuning, tune, hold, purpose),
            TunerBackend.Usb => OpenUsb(device, tuning, tune, hold, purpose),
            _ => throw BackendNeverEstablished(),
        };

    public IReceptionProbe Tune(DeviceSettings device, TuneParams tune, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return backend switch
        {
            TunerBackend.Fake => new SyntheticReceptionProbe(tune.ToLegacyRequest().PhysicalChannel),
            TunerBackend.Dvb => DvbReceptionProbe.Open(
                systemCalls.Value,
                time,
                PathsOf(device),
                DvbTuneRequest.Resolve(tune, tune.ToLegacyRequest()),
                LnbPower.For(device.Kind, device.LnbPower),
                settings,
                cancellationToken
            ),
            TunerBackend.Usb => ProbeUsb(device, DvbTuneRequest.Resolve(tune, tune.ToLegacyRequest()), cancellationToken),
            _ => throw BackendNeverEstablished(),
        };
    }

    private IReceptionProbe ProbeUsb(DeviceSettings device, DvbChannel channel, CancellationToken cancellationToken)
    {
        string path = UsbPathOf(device);

        if (usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model)
        {
            return ProbeDt295(device, path, channel, cancellationToken);
        }

        if (channel is not TerrestrialChannel terrestrial)
        {
            throw DvbFailure.Refused(
                $"devices['{device.Id}']: the usb tuner receives terrestrial broadcasting only, and a satellite channel was to be measured on it."
            );
        }

        return CechZd1ReceptionProbe.Open(
            usbHost.Value,
            path,
            DvbFrequency.TerrestrialHertz(terrestrial.PhysicalChannel),
            pacing,
            logger,
            StandbyFor(device, path),
            cancellationToken
        );
    }

    private Dt295ReceptionProbe ProbeDt295(DeviceSettings device, string path, DvbChannel channel, CancellationToken cancellationToken)
    {
        RequireTheDeclaredKind(device, channel);
        string deviceId = device.Id ?? path;
        Dt295Bridge bridge;
        bool kept;

        lock (opening)
        {
            Dt295Bridge? taken = keeper.Take(deviceId, path);

            kept = taken is not null;
            bridge = taken ?? OpenAfresh(deviceId, path);
        }

        return Dt295ReceptionProbe.Tune(
            bridge,
            channel,
            logger,
            kept ? rested => keeper.Keep(deviceId, rested) : null,
            cancellationToken
        );
    }

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

    public bool IsKeptOpen(DeviceSettings device)
    {
        if (device.Id is not { } deviceId)
        {
            return false;
        }

        lock (standing)
        {
            return keeper.Holds(deviceId)
                || (standbys.TryGetValue(deviceId, out CechZd1Standby? standby) && standby.HoldsATuner);
        }
    }

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

    private bool? PeekAtTheSlot(string path)
    {
        lock (opening)
        {
            try
            {
                using var usb = new TurnTakingConnection(usbHost.Value.Open(path, Dt295TunerDevice.Interface));

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

    private ITunerDevice OpenUsb(
        DeviceSettings device,
        TuningRequest tuning,
        TuneParams? tune,
        HeadHold hold,
        SessionPurpose? purpose
    )
    {
        string path = UsbPathOf(device);

        return usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model
            ? OpenDt295(device, path, DvbTuneRequest.Resolve(tune, tuning), hold, purpose)
            : OpenCechZd1(device, path, tuning, tune, hold, purpose);
    }

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

    private ITunerDevice OpenDt295(
        DeviceSettings device,
        string path,
        DvbChannel channel,
        HeadHold hold,
        SessionPurpose? purpose
    )
    {
        RequireTheDeclaredKind(device, channel);
        string deviceId = device.Id ?? path;
        var stages = new StageTimes(pacing.Clock);
        Dt295Bridge bridge;

        lock (opening)
        {
            bridge = keeper.Take(deviceId, path, stages) ?? OpenAfresh(deviceId, path, stages);
        }

        Dt295TunerDevice Start() =>
            Dt295TunerDevice.Start(
                bridge,
                channel,
                pacing,
                settings.BytePatience,
                logger,
                rested => keeper.Keep(deviceId, rested),
                stages,
                new TunerOpening(deviceId, device.Kind, channel, purpose)
            );

        if (descramblers.Unscrambles && descramblers is not SlotCards)
        {
            return ThroughTheCard(Start, hold, descramblers, deviceId);
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

        stages.Mark("card slot");

        return ThroughTheCard(Start, hold, slot, deviceId);
    }

    private Dt295Bridge OpenAfresh(string deviceId, string path, StageTimes? stages = null)
    {
        keeper.Admit(deviceId);

        return Dt295Bridge.Open(usbHost.Value, path, pacing, logger, dt295Handler, stages);
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

    private ITunerDevice OpenCechZd1(
        DeviceSettings device,
        string path,
        TuningRequest tuning,
        TuneParams? tune,
        HeadHold hold,
        SessionPurpose? purpose
    )
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
                    StandbyFor(device, path),
                    new TunerOpening(device.Id ?? path, device.Kind, channel, purpose)
                ),
            hold,
            Shared,
            device.Id ?? path
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

    private ITunerDevice OpenDvb(
        DeviceSettings device,
        TuningRequest tuning,
        TuneParams? tune,
        HeadHold hold,
        SessionPurpose? purpose
    )
    {
        DvbDevicePaths paths = PathsOf(device);

        return ThroughTheCard(
            () =>
            {
                DvbChannel channel = DvbTuneRequest.Resolve(tune, tuning);

                return DvbTunerDevice.Open(
                    systemCalls.Value,
                    time,
                    paths,
                    channel,
                    LnbPower.For(device.Kind, device.LnbPower),
                    settings with { ReadsFromTheLock = HeadHolds.ReadsFromTheLock(hold) },
                    CancellationToken.None,
                    logger,
                    new TunerOpening(device.Id ?? paths.Frontend, device.Kind, channel, purpose)
                );
            },
            hold,
            Shared,
            device.Id ?? paths.Frontend
        );
    }

    private ITunerDevice ThroughTheCard(
        Func<ITunerDevice> open,
        HeadHold hold,
        IDescramblerFactory cards,
        string deviceId
    )
    {
        long started = Stopwatch.GetTimestamp();
        long began = time.GetTimestamp();
        long cardAnswered = began;
        Task<IDescrambler?>? asking = cards.Unscrambles
            ? Task.Run(() =>
            {
                IDescrambler? answered = cards.Open(hold);
                Volatile.Write(ref cardAnswered, time.GetTimestamp());

                return answered;
            })
            : null;
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
        TimeSpan tunerOpen = time.GetElapsedTime(began);
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
        ReportTheCard(deviceId, tunerOpen, time.GetElapsedTime(began, Volatile.Read(ref cardAnswered)), descrambler);

        return Unscrambling(opened, descrambler, cards, hold);
    }

    private void ReportTheCard(string deviceId, TimeSpan tuned, TimeSpan card, IDescrambler? descrambler) =>
        logger?.LogInformation(
            "devices['{DeviceId}']: asking for the card ended {Card:0.0} ms after the opening began, {Apart:0.0} ms {Order} the tuner was open, and {Answer}.",
            deviceId,
            card.TotalMilliseconds,
            (tuned - card).Duration().TotalMilliseconds,
            card <= tuned ? "before" : "after",
            descrambler is null ? "no card answered" : "a card answered"
        );

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
