using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The cards in the slots of the PIX-DT295 tuners a driver drives, for a driver that has no other card. A slot is
/// reached through its tuner, so a card is asked for only as a tuner opens; the driver still says that it unscrambles,
/// and its card reader is the slot of the tuner opened last, for as long as that tuner stays open, idle or not.
/// </summary>
public sealed class SlotCards(ILogger? logger, TimeProvider? clock = null) : IDescramblerFactory, ICardReaderReport
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private Descramblers? latest;
    private Dt295Bridge? latestBridge;
    private CardIdentity? lastIdentity;

    public bool Unscrambles => true;

    public CardIdentity? CardInHand
    {
        get
        {
            lock (gate)
            {
                return latest?.CardInHand ?? lastIdentity;
            }
        }
    }

    /// <summary>
    /// Whether any device <paramref name="configuration"/> declares on the usb backend is a PIX-DT295 now.
    /// </summary>
    public static bool AreConfigured(DriverConfiguration configuration, IUsbHost host)
    {
        if (configuration.Tuner?.Backend is not TunerBackend.Usb)
        {
            return false;
        }

        return configuration.Devices?.Any(device => IsDt295(host, device)) is true;
    }

    /// <summary>
    /// The slot cards of a usb driver that borrows no card and has a PIX-DT295 among its devices, or null.
    /// </summary>
    /// <param name="host">The USB devices to look through, which are this machine's unless a test gives others.</param>
    public static SlotCards? For(
        DriverConfiguration configuration,
        bool borrows,
        ILogger? logger,
        TimeProvider? clock,
        Func<IUsbHost>? host = null
    )
    {
        if (borrows || configuration.Tuner?.Backend is not TunerBackend.Usb)
        {
            return null;
        }

        try
        {
            return AreConfigured(configuration, (host ?? (() => new LinuxUsbHost()))()) ? new SlotCards(logger, clock) : null;
        }
        catch (Exception failed) when (failed is DvbDeviceException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                "The USB devices could not be looked through for a tuner with a card slot of its own, so none is assumed: {Why}",
                failed.Message
            );

            return null;
        }
    }

    private static bool IsDt295(IUsbHost host, DeviceSettings device)
    {
        try
        {
            return UsbTunerDetector.NodeFor(host, device.Id, device.DevicePath) is { } path
                && host.ModelAt(path) == Dt295TunerDevice.Model;
        }
        catch (DvbDeviceException)
        {
            return false;
        }
    }

    /// <summary>
    /// Nothing is asked for without a tuner to reach the slot through.
    /// </summary>
    public IDescrambler? Open(HeadHold hold) => null;

    /// <summary>
    /// The cards of <paramref name="tuner"/>'s slot, which from now on are the ones this driver reports.
    /// </summary>
    public IDescramblerFactory Attach(Dt295Bridge tuner)
    {
        Descramblers slot = Descramblers.Over(tuner.CardService, logger, clock);

        lock (gate)
        {
            lastIdentity = latest?.CardInHand ?? lastIdentity;
            latest = slot;
            latestBridge = tuner;
        }

        return slot;
    }

    public CardReaderDto Inspect()
    {
        Descramblers? slot;
        bool open;

        lock (gate)
        {
            slot = latest;
            open = latestBridge?.IsOpen is true;
        }

        if (slot is null)
        {
            return new CardReaderDto { Use = CardReaderUse.Local, State = CardState.Unspecified, CheckedAt = clock.GetUtcNow() };
        }

        CardReaderDto seen = slot.Inspect();

        return open ? seen : seen with { State = CardState.Unspecified, Problem = null, Readers = [] };
    }
}
