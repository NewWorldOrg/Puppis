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
        var window = new BridgeWindow(new BridgeMemory(new TurnTakingConnection(fake)));
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

        Assert.Throws<OperationCanceledException>(() => device.Read(188, CancellationToken.None));
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

    private static (TunerDeviceFactory Factory, FakeDt295 Fake, SlotCards Slots) Driver()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        var slots = new SlotCards(null);

        return (TunerDeviceFactory.Using(Configuration(), new FakeDt295Host(fake), pacing, slots, fake.HandlerHash()), fake, slots);
    }

    private static CardReaderDto Reader(SlotCards slots) => CardReaders.Inspect(slots, Configuration(), null, TimeProvider.System);

    private static bool Claims(SlotCards slots) =>
        Ipc.DriverGreeting.Unscrambling(slots.Unscrambles).Contains(DriverCapabilities.Descrambling);

    [Fact]
    public void AUsbDriverThatBorrowsNoCardReadsThroughTheSlotsWhateverIsPluggedIn()
    {
        SlotCards slots = SlotCards.For(Configuration(), borrows: false, null, null)!;

        Assert.Same(slots, Descramblers.For(Configuration(), loan: null, logger: null, slots: slots));
        Assert.Null(SlotCards.For(Configuration(), borrows: true, null, null));
        Assert.False(Claims(slots));
        Assert.Equal(CardState.NotUsed, Reader(slots).State);
        Assert.Equal(CardReaderUse.None, Reader(slots).Use);
    }

    [Fact]
    public void ATunerPluggedInAfterTheDriverStartedBringsItsCardWithIt()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        Assert.False(Claims(slots));

        factory.Looked(Tuner, present: true);

        Assert.True(Claims(slots));
        CardReaderDto reader = Reader(slots);
        Assert.Equal(CardReaderUse.Local, reader.Use);
        Assert.Equal(CardState.Inserted, reader.State);
        Assert.Equal(Dt295CardSlot.ReaderName, Assert.Single(reader.Readers).Name);
        Assert.True(fake.Disposed);
        Assert.DoesNotContain(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt);
        factory.Dispose();
    }

    [Fact]
    public void AnEmptySlotDoesNotMakeTheDriverClaimToUnscramble()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        fake.CardPresent = false;

        factory.Looked(Tuner, present: true);

        Assert.False(Claims(slots));
        Assert.Equal(CardState.Empty, Reader(slots).State);
        factory.Dispose();
    }

    [Fact]
    public void AnIdleTunerUnpluggedIsLetGoOfAndItsCardNoLongerCounts()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme).Dispose();
        Assert.True(Claims(slots));
        Assert.NotNull(slots.CardInHand);
        fake.FailingRead = _ => true;
        fake.FailingWrite = _ => true;

        factory.Looked(Tuner, present: false);

        Assert.True(fake.Disposed);
        Assert.False(Claims(slots));
        Assert.Equal(CardState.NotUsed, Reader(slots).State);
        Assert.Null(slots.CardInHand);
        factory.Dispose();
    }

    [Fact]
    public void ATunerPluggedInAgainIsStartedAfreshAndCountsAgain()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme).Dispose();
        factory.Looked(Tuner, present: false);

        factory.Looked(Tuner, present: true);
        using (ITunerDevice device = factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme))
        {
            Assert.NotEmpty(device.Read(188 * 10, CancellationToken.None));
        }

        Assert.True(Claims(slots));
        Assert.Equal(2, fake.WritesSoFar().Count(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt && write.Data[0] is 0x9B));
        factory.Dispose();
    }

    [Fact]
    public void ADeviceThatCannotBeLookedAtThrowsNothingAndCountsForNothing()
    {
        var logger = new CapturingLogger<TunerDeviceFactory>();
        var slots = new SlotCards(null);
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        TunerDeviceFactory factory = TunerDeviceFactory.Using(Configuration(), new UnreadableHost(new IOException("bus gone")), pacing, slots, logger: logger);

        factory.Looked(Tuner, present: true);

        Assert.False(Claims(slots));
        Assert.Contains(logger.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
        factory.Dispose();
    }

    [Fact]
    public void AskingHowTheCardStandsAsksNoIdleTunerAnything()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme).Dispose();
        long before = fake.Requests;

        Assert.True(Claims(slots));
        Assert.Equal(CardState.Inserted, Reader(slots).State);
        Assert.NotNull(slots.CardInHand);

        Assert.Equal(before, fake.Requests);
        factory.Dispose();
    }

    [Fact]
    public void TheSlotIsReportedLikeAnyReaderWhileItsTunerIsInUse()
    {
        (TunerDeviceFactory factory, _, SlotCards slots) = Driver();

        ITunerDevice device = factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme);
        CardReaderDto open = Reader(slots);
        device.Dispose();

        Assert.Equal(CardState.Inserted, open.State);
        CardReaderSlotDto reader = Assert.Single(open.Readers);
        Assert.Equal(Dt295CardSlot.ReaderName, reader.Name);
        Assert.True(reader.InUse);
        Assert.NotNull(open.LastAnsweredAt);
        Assert.Equal(SyntheticCardKeys.CaSystemId, slots.CardInHand!.CaSystemId);
        factory.Dispose();
    }

    private sealed class UnreadableHost(Exception failure) : IUsbHost
    {
        public IReadOnlyList<UsbDeviceNode> List() => throw failure;

        public UsbModel ModelAt(string path) => throw failure;

        public IUsbConnection Open(string path, byte interfaceNumber) => throw failure;
    }
}

public sealed class Dt295SecondReviewTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly uint[] Endpoint = [0xFDBC04, 0xFDBC14, 0xFDBD08];

    private static Func<uint, bool> OnWrite(uint address, int nth)
    {
        int seen = 0;

        return reached => reached == address && ++seen == nth;
    }

    private static void OpenFails(FakeDt295 fake)
    {
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        Assert.Throws<DvbDeviceException>(() =>
            Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, new TerrestrialChannel(22), pacing, TimeSpan.FromSeconds(5), null, fake.HandlerHash())
        );
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

    [Fact]
    public void AWriteWindowThatFailsToCloseIsPutBackWhenTheTunerCloses()
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        fake.WritesLandBeforeFailing = false;
        fake.FailingWrite = OnWrite(BridgeWindow.WriteWindow, 2);

        OpenFails(fake);

        AssertAsFound(fake, found);
    }

    [Fact]
    public void AReadWindowThatFailsToCloseIsPutBackWhenTheTunerCloses()
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        int reads = 0;
        fake.FailingRead = reached => reached == BridgeWindow.ReadWindow && ++reads == 4;

        OpenFails(fake);

        AssertAsFound(fake, found);
    }

    [Fact]
    public void AFailureWhileTheCaptureIsPreparedIsRolledBack()
    {
        FakeDt295 fake = FakeDt295.Locking();
        uint[] found = [.. Endpoint.Select(fake.Word)];
        fake.FailingWrite = OnWrite(0x01901044, 1);

        OpenFails(fake);

        AssertAsFound(fake, found);
    }

    [Fact]
    public void AReadTheTunersClosingInterruptsEndsAsAStop()
    {
        FakeDt295 fake = FakeDt295.Locking();
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        Dt295TunerDevice device = Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, new TerrestrialChannel(22), pacing, TimeSpan.FromSeconds(5), null, fake.HandlerHash());
        using var reading = new ManualResetEventSlim();
        Exception? ended = null;
        Thread reader = new(() =>
        {
            reading.Set();

            try
            {
                device.Read(188, CancellationToken.None);
            }
            catch (Exception error)
            {
                ended = error;
            }
        });
        reader.Start();
        reading.Wait();
        SpinWait.SpinUntil(() => reader.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5));

        device.Dispose();
        reader.Join();

        Assert.IsType<OperationCanceledException>(ended);
    }

    [Fact]
    public void TheSonyTunerBesideAPixDt295IsReadBareWithoutAskingTheSlot()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        var sony = new FakeUsbHost(UsbTestSupport.BridgeWith(demodulator, tuner));
        sony.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/002", Tuning.Usb.CechZd1.CechZd1TunerDevice.Model, "1-1"));
        sony.Nodes.Add(new UsbDeviceNode(Path, Dt295TunerDevice.Model, "1-2"));
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        var sonyDevice = new DeviceSettings("usb1-1", DeviceKind.Terrestrial, "/dev/bus/usb/001/002");
        var configuration = new DriverConfiguration(
            OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
            Tuner: new TunerSettings(TunerBackend.Usb),
            Devices: [sonyDevice, new DeviceSettings("usb1-2", DeviceKind.Terrestrial, Path)]
        );
        var logger = new CapturingLogger<TunerDeviceFactory>();
        var slots = new SlotCards(null);
        TunerDeviceFactory factory = TunerDeviceFactory.Using(configuration, sony, pacing, slots, logger: logger);

        using ITunerDevice device = factory.Create(sonyDevice, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme);

        Assert.IsNotType<DescramblingTunerDevice>(device);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("No card answered", StringComparison.Ordinal));
    }
}
