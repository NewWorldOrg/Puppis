using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class Dt295KeptTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, Path);

    private static readonly uint[] StartUp = [Dt295Startup.GpioDirection, Dt295Startup.GpioValue, 0xF10118, 0xF1011C, 0xF001F0, 0xF10114, CopyProgram.HookAt];

    private static (TunerDeviceFactory Factory, FakeDt295 Fake) Factory(IDescramblerFactory? descramblers = null)
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        return (
            TunerDeviceFactory.Using(
                new DriverConfiguration(
                    OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                    Tuner: new TunerSettings(TunerBackend.Usb),
                    Devices: [Tuner]
                ),
                new FakeDt295Host(fake),
                pacing,
                descramblers,
                fake.HandlerHash()
            ),
            fake
        );
    }

    private static ITunerDevice Session(TunerDeviceFactory factory, int channel = 22) =>
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, channel), null, HeadHold.EveryProgramme);

    private static void AssertGivenBack(FakeDt295 fake)
    {
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.Equal(CopyProgram.UsbGroupAtRest, fake.Word(CopyProgram.UsbGroup));
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.True(fake.Disposed);
    }

    private static bool IsStartUp(UsbSetup setup, byte[] data) =>
        StartUp.Contains(FakeDt295.AddressOf(setup))
        || FakeDt295.AddressOf(setup) is >= CopyProgram.Entry and < CopyProgram.Entry + CopyProgram.PlaceBytes;

    [Fact]
    public void AClosedSessionLeavesTheDeviceOpenWithTheProgramHookedAndTheStreamStopped()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();

        Session(factory).Dispose();

        Assert.False(fake.Disposed);
        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        Assert.Equal(0x04000000u, fake.Word(CopyProgram.CaptureRunning));
        Assert.Equal(0x000403FAu, fake.Word(0xFDBC14));
        Assert.True(fake.BulkClosed);
        Assert.Equal(0x90, fake.Demodulator[0x03]);
        Assert.Equal(0x00, fake.TerrestrialTuner[0x12]);
        Assert.Equal(0u, fake.Word(CardUart.Power));
        factory.Dispose();
    }

    [Fact]
    public void TheNextSessionOnAKeptDeviceOnlyTunesAndStartsTheStream()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        Session(factory).Dispose();
        int writesBefore = fake.WritesSoFar().Count;
        fake.Log.Clear();
        int ready = 0;

        using (ITunerDevice device = Session(factory, 27))
        {
            Assert.NotEmpty(device.Read(188 * 10, CancellationToken.None));
            ready = fake.WritesSoFar().Count;
        }

        Assert.DoesNotContain(fake.WritesSoFar().Skip(writesBefore).Take(ready - writesBefore), write => IsStartUp(write.Setup, write.Data));
        Assert.DoesNotContain("20 w[0100]", PixDt295Support.Written(fake));
        Assert.DoesNotContain("20 w[FEC0FF]", PixDt295Support.Written(fake));
        Assert.Contains("20 w[FEC01049]", PixDt295Support.Written(fake));
        Assert.Equal(1, fake.WritesSoFar().Count(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt));
        factory.Dispose();
    }

    [Fact]
    public void AKeptDeviceThatIsNoLongerAsItWasLeftIsClosedAndOpenedAfreshOnce()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        Session(factory).Dispose();
        fake.Word(CopyProgram.HookAt, CopyProgram.HookAtPowerOn[0]);
        fake.Word(CopyProgram.HookAt + 4, CopyProgram.HookAtPowerOn[1]);

        using (ITunerDevice device = Session(factory))
        {
            Assert.NotEmpty(device.Read(188 * 10, CancellationToken.None));
        }

        Assert.Equal(2, fake.WritesSoFar().Count(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt && write.Data[0] is 0x9B));
        factory.Dispose();
    }

    [Fact]
    public void StoppingTheDriverGivesAKeptDeviceBackAsItWasFound()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        Session(factory).Dispose();

        factory.Dispose();

        AssertGivenBack(fake);
    }

    [Fact]
    public void ADeviceThatFailedIsClosedRatherThanKept()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        ITunerDevice device = Session(factory);
        fake.InstructionsTimeOut = true;

        Assert.Throws<DvbDeviceException>(() => device.Read(188, CancellationToken.None));
        fake.InstructionsTimeOut = false;
        device.Dispose();

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void LettingGoOfADisabledDeviceClosesIt()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        Session(factory).Dispose();

        factory.LetGo(Tuner);

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void AChannelThatDoesNotLockLeavesTheDeviceKept()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        fake.Demodulator[0x80] = 0xAF;

        Assert.Equal(TuningFailure.NoLock, Assert.Throws<DvbDeviceException>(() => Session(factory)).Failure);

        Assert.False(fake.Disposed);
        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        fake.Demodulator[0x80] = 0x06;
        Session(factory).Dispose();
        Assert.Equal(1, fake.WritesSoFar().Count(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt));
        factory.Dispose();
        AssertGivenBack(fake);
    }

    [Fact]
    public void CheckingAKeptDeviceAsksItWithoutOpeningItAgain()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Factory();
        Session(factory).Dispose();
        int writes = fake.WritesSoFar().Count;

        factory.Check(Tuner);

        Assert.DoesNotContain(fake.WritesSoFar().Skip(writes), write => IsStartUp(write.Setup, write.Data));
        Assert.False(fake.Disposed);
        fake.Word(CopyProgram.UsbGroup, 6);
        Assert.Throws<DvbDeviceException>(() => factory.Check(Tuner));
        factory.Dispose();
    }

    [Fact]
    public void ADisabledTunerIsLetGoOf()
    {
        var check = new RecordingCheck();
        var manager = new TunerSessionManager(
            new DriverConfiguration(
                "/run/carina/driver.sock",
                [],
                6,
                new TunerSettings(TunerBackend.Fake),
                [new DeviceSettings("adapter0", DeviceKind.Terrestrial)]
            ),
            new TunerDeviceFactory(new DriverConfiguration("/run/carina/driver.sock", [], 6, new TunerSettings(TunerBackend.Fake), []), TimeProvider.System),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            deviceCheck: check
        );

        manager.Turn("adapter0", disabled: false);
        manager.Turn("adapter0", disabled: true);

        Assert.Equal(["adapter0"], check.LetGoOf);
    }

    [Fact]
    public void WhatThePatrolFindsIsHandedOn()
    {
        var device = new DeviceSettings("usb1-2", DeviceKind.Terrestrial, Path);
        var configuration = new DriverConfiguration("/run/carina/driver.sock", [], 6, new TunerSettings(TunerBackend.Usb), [device]);
        List<string> letGo = [];
        var patrol = new TunerPatrol(
            configuration,
            new TunerHardwareBook(configuration, TimeProvider.System),
            new Absent(),
            () => [],
            TimeProvider.System,
            null,
            (seen, present) => letGo.Add($"{seen.Id} {present}")
        );

        patrol.Walk();

        Assert.Equal(["usb1-2 False"], letGo);
    }

    [Fact]
    public void ALookAtOneTunerOutsideTheWalkIsHandedOnTheSameWayAndAnswered()
    {
        var device = new DeviceSettings("usb1-2", DeviceKind.Terrestrial, Path);
        var configuration = new DriverConfiguration("/run/carina/driver.sock", [], 6, new TunerSettings(TunerBackend.Usb), [device]);
        var book = new TunerHardwareBook(configuration, TimeProvider.System);
        List<string> handedOn = [];
        var patrol = new TunerPatrol(
            configuration,
            book,
            new Absent(),
            () => [],
            TimeProvider.System,
            null,
            (seen, present) => handedOn.Add($"{seen.Id} {present}")
        );

        Assert.False(patrol.LookAt(device));
        Assert.Equal(["usb1-2 False"], handedOn);
        Assert.False(book.For(device).Present);
    }

    private sealed class RecordingCheck : ITunerDeviceCheck
    {
        private readonly ConcurrentQueue<string> letGo = new();

        public IReadOnlyList<string> LetGoOf => [.. letGo];

        public void Check(DeviceSettings device) { }

        public void LetGo(DeviceSettings device) => letGo.Enqueue(device.Id!);
    }

    private sealed class Absent : ITunerPresence
    {
        public bool IsPresent(DeviceSettings device, TunerHardwareDto hardware) => false;
    }
}

public sealed class Dt295KeptReviewTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, Path);

    private static DriverConfiguration Configuration() =>
        new(
            OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
            Tuner: new TunerSettings(TunerBackend.Usb),
            Devices: [Tuner]
        );

    private static (TunerDeviceFactory Factory, FakeDt295 Fake, SlotCards Slots, CapturingLogger<TunerDeviceFactory> Logger) Driver()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        var slots = new SlotCards(null);
        var logger = new CapturingLogger<TunerDeviceFactory>();

        return (TunerDeviceFactory.Using(Configuration(), new FakeDt295Host(fake), pacing, slots, fake.HandlerHash(), logger), fake, slots, logger);
    }

    private static ITunerDevice Session(TunerDeviceFactory factory, int channel = 22) =>
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, channel), null, HeadHold.EveryProgramme);

    private static void AssertGivenBack(FakeDt295 fake)
    {
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.Equal(CopyProgram.UsbGroupAtRest, fake.Word(CopyProgram.UsbGroup));
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.True(fake.Disposed);
    }

    private static int HooksWritten(FakeDt295 fake) =>
        fake.WritesSoFar().Count(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt && write.Data[0] is 0x9B);

    [Fact]
    public void ASlotThatCannotBeReadAsASessionStartsLeavesTheDeviceGivenBack()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        fake.FailingRead = reached => reached == CardUart.Status;

        Assert.Throws<DvbDeviceException>(() => Session(factory));

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void AnIdleKeptTunersReaderIsNotInUse()
    {
        (TunerDeviceFactory factory, _, SlotCards slots, _) = Driver();
        ITunerDevice device = Session(factory);
        Assert.True(Assert.Single(slots.Inspect().Readers).InUse);

        device.Dispose();

        CardReaderSlotDto reader = Assert.Single(slots.Inspect().Readers);
        Assert.False(reader.InUse);
        Assert.Equal(CardState.Inserted, reader.State);
        factory.Dispose();
    }

    [Fact]
    public void ATunerLetGoOfDuringASessionIsClosedWhenTheSessionEnds()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        ITunerDevice device = Session(factory);

        factory.LetGo(Tuner);
        device.Dispose();

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void AFaultedTunerIsCheckedByOpeningItAfreshAndIsKeptAgainAfterwards()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        Session(factory).Dispose();
        factory.LetGo(Tuner);
        AssertGivenBack(fake);

        factory.Check(Tuner);

        Assert.Equal(2, HooksWritten(fake));
        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        Session(factory).Dispose();
        Assert.Equal(2, HooksWritten(fake));
        factory.Dispose();
    }

    [Fact]
    public void EachSessionPowersTheCardAfreshAndReadsItsAnswerToReset()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots, _) = Driver();
        Session(factory).Dispose();
        Assert.Equal(0u, fake.Word(CardUart.Power));
        fake.Card.CardId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

        using (ITunerDevice device = Session(factory))
        {
            Assert.Equal("010203040506", slots.CardInHand!.CardId);
        }

        Assert.Equal(2, fake.WordsWrittenTo(CardUart.Power).Count(power => power is 1));
        factory.Dispose();
    }

    [Fact]
    public void ACheckThatThrowsOnAKeptTunerClosesItAndOpensItAfresh()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        Session(factory).Dispose();
        bool failed = false;
        fake.FailingRead = reached => reached == CopyProgram.HookAt && !failed && (failed = true);

        using (ITunerDevice device = Session(factory))
        {
            Assert.NotEmpty(device.Read(188 * 10, CancellationToken.None));
        }

        Assert.Equal(2, HooksWritten(fake));
        Assert.Contains(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt && write.Data[0] is 0xC4);
        factory.Dispose();
    }

    [Fact]
    public void ATuneThatFailsOnAKeptTunerClosesItAndGivesItBack()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        Session(factory).Dispose();
        fake.Acknowledging = false;

        Assert.Equal(TuningFailure.DeviceUnusable, Assert.Throws<DvbDeviceException>(() => Session(factory)).Failure);

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void ARestoreStepThatFailsDoesNotStopTheOthers()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _, _) = Driver();
        Session(factory).Dispose();
        fake.FailingWrite = reached => reached == CopyProgram.HookAt;

        factory.Dispose();

        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void ATunerGoneWhoseClosingThrowsDoesNotStopThePatrol()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots, CapturingLogger<TunerDeviceFactory> logger) = Driver();
        Session(factory).Dispose();
        fake.DisposingThrows = new ArgumentException("synthetic");

        factory.Looked(Tuner, present: false);

        Assert.False(slots.Unscrambles);
        Assert.Contains(logger.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
        fake.DisposingThrows = null;
        factory.Dispose();
    }
}

public sealed class Dt295KeptRaceTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, Path);

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

    private static ITunerDevice Session(TunerDeviceFactory factory) =>
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme);

    private static void AssertGivenBack(FakeDt295 fake)
    {
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.True(fake.Disposed);
    }

    private static void LetGoAsItOpens(FakeDt295 fake, TunerDeviceFactory factory)
    {
        bool done = false;
        fake.BeforeWrite = reached =>
        {
            if (reached == Dt295Startup.GpioDirection && !done)
            {
                done = true;
                factory.LetGo(Tuner);
            }
        };
    }

    [Fact]
    public void ATunerLetGoOfWhileItOpensForASessionIsClosedWhenTheSessionEnds()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _) = Driver();
        LetGoAsItOpens(fake, factory);

        Session(factory).Dispose();

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void ATunerLetGoOfWhileTheRecheckOpensItIsClosedRatherThanKept()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _) = Driver();
        LetGoAsItOpens(fake, factory);

        factory.Check(Tuner);

        AssertGivenBack(fake);
        factory.Dispose();
    }

    [Fact]
    public void ACardRequestLeftOverFromASessionThatEndedNeverReachesTheCard()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        Dt295Bridge bridge = Dt295Bridge.Open(new FakeDt295Host(fake), Path, pacing, null, fake.HandlerHash());
        Dt295Bridge? kept = null;
        int first = bridge.Slot.BeginSession();
        ISmartCardService late = bridge.Slot.Service(first);
        ConditionalAccessCard inFlight = ConditionalAccessCard.Open(bridge.Slot.Service(first));
        Dt295TunerDevice device = Dt295TunerDevice.Start(bridge, new TerrestrialChannel(22), pacing, TimeSpan.FromSeconds(5), null, rested => kept = rested);

        device.Dispose();
        int powerOns = fake.WordsWrittenTo(CardUart.Power).Count(power => power is 1);

        Assert.Same(bridge, kept);
        Assert.Throws<DescramblingException>(() => ConditionalAccessCard.Open(late));
        Assert.Throws<DescramblingException>(() => inFlight.Answer([0x01, 0x02, 0x03]));
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.Equal(powerOns, fake.WordsWrittenTo(CardUart.Power).Count(power => power is 1));

        int second = bridge.Slot.BeginSession();
        using (ConditionalAccessCard card = ConditionalAccessCard.Open(bridge.Slot.Service(second)))
        {
            Assert.True(card.Answer([0x01, 0x02, 0x03]).Granted);
        }

        Assert.Equal(powerOns + 1, fake.WordsWrittenTo(CardUart.Power).Count(power => power is 1));
        bridge.Close();
    }

    [Fact]
    public async Task HowTheCardStandsIsAnsweredWhileTheTunerIsBusyStartingItsStream()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, SlotCards slots) = Driver();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fake.BeforeWrite = reached =>
        {
            if (reached == 0x01901044)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };
        Task<ITunerDevice> starting = Task.Run(() => Session(factory));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(30)));

        Task<CardReaderDto> asked = Task.Run(slots.Inspect);
        Task first = await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(5)));
        release.Set();

        Assert.Same(asked, first);
        fake.BeforeWrite = null;
        (await starting).Dispose();
        factory.Dispose();
    }

    [Fact]
    public void ARestThatThrowsSomethingUnexpectedClosesTheTuner()
    {
        (TunerDeviceFactory factory, FakeDt295 fake, _) = Driver();
        ITunerDevice device = Session(factory);
        bool thrown = false;
        fake.BeforeWrite = reached =>
        {
            if (reached == BridgeI2c.Data && !thrown)
            {
                thrown = true;

                throw new ArgumentException("synthetic");
            }
        };

        device.Dispose();

        fake.BeforeWrite = null;
        AssertGivenBack(fake);
        factory.Dispose();
    }
}
