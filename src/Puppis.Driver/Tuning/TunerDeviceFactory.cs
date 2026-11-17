using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
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
}

public sealed class TunerDeviceFactory : ITunerDeviceFactory, ITunerDeviceCheck, IDisposable
{
    private readonly CechZd1Standby standby = new();
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
        string dt295Handler = Dt295Startup.HandlerAtPowerOn
    ) =>
        new(
            configuration,
            TimeProvider.System,
            new Lazy<IDvbSystemCalls>(() => new LinuxDvbSystemCalls()),
            new Lazy<IUsbHost>(() => usbHost),
            pacing,
            descramblers ?? NoDescrambling.Instance,
            null,
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
                CheckUsb(UsbPathOf(device));

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

    private void CheckUsb(string path)
    {
        if (usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model)
        {
            usbHost.Value.Open(path, Dt295TunerDevice.Interface).Dispose();

            return;
        }

        CechZd1TunerDevice.OpenAndClose(usbHost.Value, path, standby);
    }

    private ITunerDevice OpenUsb(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
    {
        string path = UsbPathOf(device);

        return usbHost.Value.ModelAt(path) == Dt295TunerDevice.Model
            ? OpenDt295(device, path, DvbTuneRequest.Resolve(tune, tuning), hold)
            : OpenCechZd1(device, path, tuning, tune, hold);
    }

    /// <summary>
    /// Opens a PIX-DT295, read through the card a lender holds where there is one and through the card in the tuner's
    /// own slot where there is not.
    /// </summary>
    private ITunerDevice OpenDt295(DeviceSettings device, string path, DvbChannel channel, HeadHold hold)
    {
        RequireTheDeclaredKind(device, channel);

        Dt295TunerDevice Open() =>
            Dt295TunerDevice.Open(usbHost.Value, path, channel, pacing, settings.BytePatience, logger, dt295Handler);

        if (descramblers.Unscrambles && descramblers is not SlotCards)
        {
            return ThroughTheCard(Open, hold);
        }

        Dt295TunerDevice opened = Open();

        try
        {
            IDescramblerFactory slot = descramblers is SlotCards slots
                ? slots.Attach(opened)
                : Descramblers.Over(opened.CardService, logger, time);

            return Unscrambling(opened, slot.Open(hold), slot, hold);
        }
        catch
        {
            opened.Dispose();

            throw;
        }
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
                    standby
                ),
            hold
        );
    }

    public void Dispose() => standby.Dispose();

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
            hold
        );
    }

    /// <summary>
    /// Tunes the device while the card is asked for, so that the card's answer to the initial setting is not added to
    /// the time the frontend takes to lock. A card that answers after the tuning has failed is let go of.
    /// </summary>
    private ITunerDevice ThroughTheCard(Func<ITunerDevice> open, HeadHold hold)
    {
        long started = Stopwatch.GetTimestamp();
        Task<IDescrambler?>? asking = descramblers.Unscrambles ? Task.Run(() => descramblers.Open(hold)) : null;
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

        return Unscrambling(opened, descrambler, hold);
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

    private ITunerDevice Unscrambling(ITunerDevice opened, IDescrambler? descrambler, HeadHold hold) =>
        Unscrambling(opened, descrambler, descramblers, hold);

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
