using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

/// <summary>
/// A device that fails part way through opening is given back as it was found.
/// </summary>
public sealed class Dt295RestoringTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly uint[] Endpoint = [0xFDBC04, 0xFDBC14, 0xFDBD08];

    private static Func<uint, bool> Once(uint address)
    {
        bool failed = false;

        return reached =>
        {
            if (reached != address || failed)
            {
                return false;
            }

            failed = true;

            return true;
        };
    }

    private static (DvbDeviceException Refused, CapturingLogger<Dt295TunerDevice> Logger) Open(FakeDt295 fake)
    {
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        var logger = new CapturingLogger<Dt295TunerDevice>();
        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() =>
            Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, new TerrestrialChannel(22), pacing, TimeSpan.FromSeconds(5), logger, fake.HandlerHash())
        );

        return (refused, logger);
    }

    private static void AssertAsFound(FakeDt295 fake, uint[] endpoint)
    {
        Assert.Equal(endpoint, Endpoint.Select(fake.Word));
        Assert.Equal(CopyProgram.UsbGroupAtRest, fake.Word(CopyProgram.UsbGroup));
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData(0xFDBD08u, false)]
    [InlineData(0xFDBD08u, true)]
    [InlineData(0xFDBC14u, false)]
    [InlineData(0xFDBC14u, true)]
    public void AnEndpointWriteThatFailsIsRolledBackWithEverythingElse(uint address, bool landed)
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        fake.WritesLandBeforeFailing = landed;
        fake.FailingWrite = Once(address);

        Open(fake);

        AssertAsFound(fake, found);
    }

    [Fact]
    public void PuttingTheEndpointBackCarriesOnPastAWriteThatKeepsFailingAndSaysSo()
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        fake.FailingWrite = reached => reached is 0xFDBD08;

        (_, CapturingLogger<Dt295TunerDevice> logger) = Open(fake);

        AssertAsFound(fake, found);
        Assert.Contains(logger.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains("putting the endpoint back", StringComparison.Ordinal) && line.Contains(Path, StringComparison.Ordinal));
    }

    [Fact]
    public void AHookWriteThatLandsAndThenFailsIsTakenOutAgain()
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        fake.WritesLandBeforeFailing = true;
        fake.FailingWrite = Once(CopyProgram.HookAt);

        Open(fake);

        AssertAsFound(fake, found);
    }

    [Fact]
    public void AWindowWordThatCannotBeReadBackIsPutBack()
    {
        FakeDt295 fake = FakeDt295.Locking();
        var window = new BridgeWindow(new BridgeMemory(fake));
        int reads = 0;
        fake.FailingRead = reached => reached == BridgeWindow.ReadWindow && ++reads is 2;

        Assert.Throws<DvbDeviceException>(() => window.Read(0x01902028));

        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
    }

    [Fact]
    public void AClosedTunerIsNotAskedForAnything()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        Dt295TunerDevice device = Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, new TerrestrialChannel(22), pacing, TimeSpan.FromSeconds(5), null, fake.HandlerHash());
        device.Dispose();
        long before = fake.Requests;

        Assert.Throws<ObjectDisposedException>(() => device.Read(188, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => device.Measure());
        Assert.Equal(before, fake.Requests);
    }

    [Fact]
    public void ACardThatKeepsAskingForMoreTimeIsGivenUpOn()
    {
        var protocol = new T1Protocol(_ => T1Protocol.Block(T1Protocol.WaitRequest, [0x01]), 0x7C);

        Assert.Equal(SmartCardCodes.CommunicationLost, Assert.Throws<SmartCardException>(() => protocol.Transmit([0x90])).Code);
    }

    [Fact]
    public void AFrequencyAboveEveryBandIsRefusedNamingTheDevice()
    {
        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() => SatellitePlan.For(2_200_000, Path));

        Assert.StartsWith(Path, refused.Message, StringComparison.Ordinal);
    }
}

public sealed class Dt295DeclaredKindTests
{
    private static TunerDeviceFactory Factory(FakeDt295 fake, DeviceSettings device)
    {
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        return TunerDeviceFactory.Using(
            new DriverConfiguration(
                OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                Tuner: new TunerSettings(TunerBackend.Usb),
                Devices: [device]
            ),
            new FakeDt295Host(fake),
            pacing,
            null,
            fake.HandlerHash()
        );
    }

    [Theory]
    [InlineData(DeviceKind.Terrestrial, TunerKind.Satellite, 2)]
    [InlineData(DeviceKind.Satellite, TunerKind.Terrestrial, 22)]
    public void ATuneOfTheOtherKindIsRefusedWithoutOpeningTheTuner(DeviceKind declared, TunerKind asked, int channel)
    {
        FakeDt295 fake = FakeDt295.Locking();
        var device = new DeviceSettings("usb1-2", declared, "/dev/bus/usb/001/005");

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() =>
            Factory(fake, device).Create(device, new TuningRequest(asked, channel), null, HeadHold.EveryProgramme)
        );

        Assert.StartsWith("devices['usb1-2']:", refused.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Writes);
    }
}

public sealed class SlotCardsTests
{
    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, "/dev/bus/usb/001/005");

    private static DriverConfiguration Configuration() =>
        new(
            OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
            Tuner: new TunerSettings(TunerBackend.Usb),
            Devices: [Tuner]
        );

    [Fact]
    public void ADriverWithAPixDt295AndNoLoanUnscramblesWithTheSlot()
    {
        var host = new FakeDt295Host(FakeDt295.Locking());
        host.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/005", Dt295TunerDevice.Model, "1-2"));
        var slots = new SlotCards(null);

        Assert.True(SlotCards.AreConfigured(Configuration(), host));
        Assert.Same(slots, Descramblers.For(Configuration(), loan: null, logger: null, slots: slots));
        Assert.True(slots.Unscrambles);
        Assert.Contains(DriverCapabilities.Descrambling, Ipc.DriverGreeting.Unscrambling(slots.Unscrambles));
    }

    [Fact]
    public void ADriverWithOnlyTheSonyTunerHasNoSlotCards()
    {
        var host = new FakeDt295Host(FakeDt295.Locking());
        host.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/005", Tuning.Usb.CechZd1.CechZd1TunerDevice.Model, "1-2"));

        Assert.False(SlotCards.AreConfigured(Configuration(), host));
        Assert.Same(NoDescrambling.Instance, Descramblers.For(Configuration(), loan: null, logger: null));
    }

    [Fact]
    public void TheSlotIsReportedLikeAnyReaderWhileItsTunerIsOpenAndItsCardIsRemembered()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        var slots = new SlotCards(null);
        TunerDeviceFactory factory = TunerDeviceFactory.Using(Configuration(), new FakeDt295Host(fake), pacing, slots, fake.HandlerHash());

        CardReaderDto before = CardReaders.Inspect(slots, Configuration(), null, TimeProvider.System);
        Assert.Equal(CardReaderUse.Local, before.Use);
        Assert.Equal(CardState.Unspecified, before.State);
        Assert.Null(slots.CardInHand);

        ITunerDevice device = factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme);
        CardReaderDto open = CardReaders.Inspect(slots, Configuration(), null, TimeProvider.System);
        device.Dispose();
        CardReaderDto closed = CardReaders.Inspect(slots, Configuration(), null, TimeProvider.System);

        Assert.Equal(CardState.Inserted, open.State);
        CardReaderSlotDto reader = Assert.Single(open.Readers);
        Assert.Equal(Dt295CardSlot.ReaderName, reader.Name);
        Assert.True(reader.InUse);
        Assert.NotNull(open.LastAnsweredAt);
        Assert.Equal(SyntheticCardKeys.CaSystemId, slots.CardInHand!.CaSystemId);
        Assert.Equal(CardState.Unspecified, closed.State);
        Assert.Equal(open.LastAnsweredAt, closed.LastAnsweredAt);
        Assert.NotNull(slots.CardInHand);
    }
}
