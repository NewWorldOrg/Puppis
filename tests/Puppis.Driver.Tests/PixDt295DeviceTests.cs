using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Transport;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.CechZd1;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class Dt295TunerDeviceTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private static readonly TerrestrialChannel Channel22 = new(22);

    private static readonly int UnitsPerTransfer = Dt295Capture.TransferBytes / CaptureUnpacker.UnitBytes;

    private static (Dt295TunerDevice Device, FakeDt295 Fake, SteppedClock Clock) Opened(FakeDt295? fake = null, bool broadcasting = true)
    {
        fake ??= FakeDt295.Locking();
        fake.Broadcasting = broadcasting;
        (DevicePacing pacing, SteppedClock clock) = PixDt295Support.Pacing(fake);
        Dt295TunerDevice device = Dt295TunerDevice.Open(
            new FakeDt295Host(fake),
            Path,
            Channel22,
            pacing,
            TimeSpan.FromSeconds(5),
            null,
            fake.HandlerHash()
        );

        return (device, fake, clock);
    }

    private static DvbDeviceException Refused(FakeDt295 fake)
    {
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        return Assert.Throws<DvbDeviceException>(() =>
            Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, Channel22, pacing, TimeSpan.FromSeconds(5), null, fake.HandlerHash())
        );
    }

    private static void AssertLeftAsAtPowerOn(FakeDt295 fake)
    {
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, fake.Word(BridgeWindow.WriteWindow));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void OpeningStartsTheStreamWithReadsWaitingAndTheProgramHookedIn()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened();

        Assert.Equal(((byte)0x81, 64_512, 16), fake.BulkOpened);
        Assert.Equal(CopyProgram.Running, fake.Word(CopyProgram.CaptureRunning));
        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        Assert.Single(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt && write.Data.Length is 8);
        Assert.Equal(0x00FF6000u, fake.Word(0xFDBD08));
        Assert.Equal(0x000003F8u, fake.Word(0xFDBC14) & 0xFFFF);
        device.Dispose();
    }

    private static void NoteTheStreamBeingReadied(FakeDt295 fake) =>
        fake.BeforeWrite = reached =>
        {
            string? step = reached switch
            {
                Dt295Capture.Clocked => "capture set-up begins",
                0x01902000 => "capture set-up ends",
                0xFDBC04 => "endpoint set-up",
                CopyProgram.CaptureRunning => "intake",
                _ => null,
            };

            if (step is not null && !fake.Log.Contains(step))
            {
                fake.Log.Add(step);
            }
        };

    [Fact]
    public void TheStreamIsReadiedWhileTheTunerSettlesAndLocksSoOnlyTheIntakeFollowsTheLock()
    {
        FakeDt295 fake = FakeDt295.Locking();
        NoteTheStreamBeingReadied(fake);

        (Dt295TunerDevice device, _, _) = Opened(fake);

        IReadOnlyList<string> written = PixDt295Support.Written(fake);
        int settling = written.ToList().LastIndexOf("20 w[234C]");
        Assert.Equal(
            [
                "20 w[234C]",
                "capture set-up begins",
                "capture set-up ends",
                "wait 100",
                "20 w[1D02]",
                "20 w[1E2A]",
                "20 w[1F8A]",
                "endpoint set-up",
                "bulk reads",
                "wait 10",
                "20 w[80]",
                "20 w[B0]",
                "intake",
            ],
            written.Skip(settling).Take(13)
        );
        device.Dispose();
    }

    [Fact]
    public void TheIntakeIsOpenedOnlyOnceTheDemodulatorNoLongerFlagsErrors()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.TerrestrialStatusReads.Enqueue(0x2E);
        fake.TerrestrialStatusReads.Enqueue(0x06);
        fake.TerrestrialStatusReads.Enqueue(0x06);
        fake.TerrestrialStatusReads.Enqueue(0x04);
        NoteTheStreamBeingReadied(fake);

        (Dt295TunerDevice device, _, _) = Opened(fake);

        IReadOnlyList<string> written = PixDt295Support.Written(fake);
        Assert.Equal(
            [
                "bulk reads",
                "wait 10",
                "20 w[80]",
                "20 w[B0]",
                "wait 10",
                "20 w[80]",
                "20 w[B0]",
                "wait 10",
                "20 w[80]",
                "wait 10",
                "20 w[80]",
                "intake",
            ],
            written.Skip(written.ToList().IndexOf("bulk reads")).Take(12)
        );
        Assert.Empty(fake.TerrestrialStatusReads);
        device.Dispose();
    }

    [Fact]
    public void ASatelliteTuneReadiesItsStreamOnlyAfterItsLockAsItAlwaysDid()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Broadcasting = true;
        fake.Satellite[0xE6] = 0x40;
        fake.Satellite[0xE7] = 0x10;
        NoteTheStreamBeingReadied(fake);
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        Dt295TunerDevice device = Dt295TunerDevice.Open(
            new FakeDt295Host(fake),
            Path,
            new BroadcastSatelliteChannel(1, 0x4010),
            pacing,
            TimeSpan.FromSeconds(5),
            null,
            fake.HandlerHash()
        );

        List<string> written = [.. PixDt295Support.Written(fake)];
        Assert.Equal(
            ["22 w[C3]", "22 w[E6]", "capture set-up begins", "capture set-up ends", "endpoint set-up", "bulk reads", "intake"],
            written.Skip(written.LastIndexOf("22 w[C3]")).Take(7)
        );
        device.Dispose();
    }

    [Fact]
    public void ReadsHandOnWholePacketsOutOfTheRing()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened();

        byte[] packets = device.Read(TsPacketReader.PacketLength * 1000, CancellationToken.None);

        Assert.True(packets.Length >= TsPacketReader.PacketLength);
        Assert.Equal(0, packets.Length % TsPacketReader.PacketLength);
        Assert.All(packets.Chunk(TsPacketReader.PacketLength), packet => Assert.Equal(0x47, packet[0]));
        Assert.Equal(0, device.Overflows);
        device.Dispose();
    }

    [Fact]
    public void ClosingStopsTheStreamAndGivesTheBridgeBackAsItWasFound()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened();

        device.Dispose();

        AssertLeftAsAtPowerOn(fake);
        Assert.True(fake.BulkClosed);
        Assert.Equal(0x04000000u, fake.Word(CopyProgram.CaptureRunning));
        Assert.Equal(0u, fake.Word(0x01901040));
        Assert.Equal(0x85000000u, fake.Word(0xFDBC04));
        Assert.Equal(0x000403FAu, fake.Word(0xFDBC14));
        Assert.Equal(0x00FF6000u, fake.Word(0xFDBD08));
        Assert.Equal(CopyProgram.UsbGroupAtRest, fake.Word(CopyProgram.UsbGroup));
        Assert.Equal(0x200u, fake.Word(CardUart.Control));
        Assert.Equal(0u, fake.Word(CardUart.Power));
    }

    [Fact]
    public void ABridgeSomethingElseHasSetUpIsNotTouched()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Word(0xF02008, 0x00010000);

        DvbDeviceException refused = Refused(fake);

        Assert.Contains("plug it back in", refused.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Writes);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void RequestHandlingThatIsNotTheKnownCodeIsNotTouched()
    {
        FakeDt295 fake = FakeDt295.Locking();
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        Assert.Throws<DvbDeviceException>(() =>
            Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, Channel22, pacing, TimeSpan.FromSeconds(5))
        );
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void AHookItDoesNotKnowAsksForTheTunerToBeRepluggedAndIsLeftAsFound()
    {
        FakeDt295 fake = FakeDt295.Locking();
        string known = fake.HandlerHash();
        fake.Word(CopyProgram.HookAt, 0x9BEC1800);
        fake.Word(CopyProgram.HookAt + 4, 0x970C9FA0);
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() =>
            Dt295TunerDevice.Open(new FakeDt295Host(fake), Path, Channel22, pacing, TimeSpan.FromSeconds(5), null, known)
        );

        Assert.Contains("plug it back in", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0x9BEC1800u, fake.Word(CopyProgram.HookAt));
        Assert.DoesNotContain(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt);
    }

    [Fact]
    public void OurHookOverAProgramThatIsNoLongerOursIsTakenOutBeforeTheProgramIsWritten()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Word(CopyProgram.HookAt, CopyProgram.Hook[0]);
        fake.Word(CopyProgram.HookAt + 4, CopyProgram.Hook[1]);
        Fr30Program older = CopyProgram.Build();

        for (int word = 0; word < older.Words.Count; word++)
        {
            fake.Word(CopyProgram.Entry + (uint)(4 * word), older.Words[word]);
        }

        fake.Word(CopyProgram.Entry + (uint)(4 * (older.Words.Count - 1)), 0x970C0000);

        (Dt295TunerDevice device, _, _) = Opened(fake);

        List<uint> hookWrites = [.. fake.WritesSoFar().Where(write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt).Select(write => write.Data[0] == 0x9B ? 1u : 0u)];
        Assert.Equal([0u, 1u], hookWrites);
        device.Dispose();
    }

    [Fact]
    public void AProgramAndHookAlreadyInPlaceAreNotWrittenAgain()
    {
        (Dt295TunerDevice first, FakeDt295 fake, _) = Opened();
        first.Dispose();
        fake.Word(CopyProgram.HookAt, CopyProgram.Hook[0]);
        fake.Word(CopyProgram.HookAt + 4, CopyProgram.Hook[1]);
        fake.Writes.Clear();

        (Dt295TunerDevice second, _, _) = Opened(fake);

        Assert.DoesNotContain(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) is >= CopyProgram.Entry and < CopyProgram.Entry + CopyProgram.PlaceBytes);
        second.Dispose();
    }

    [Fact]
    public void AChannelThatDoesNotLockIsNoLockAndTheBridgeIsGivenBack()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Demodulator[0x80] = 0xAF;

        DvbDeviceException refused = Refused(fake);

        Assert.Equal(TuningFailure.NoLock, refused.Failure);
        AssertLeftAsAtPowerOn(fake);
    }

    [Fact]
    public void AChannelThatDoesNotLockHasWhatWasReadiedForItsStreamPutBack()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Demodulator[0x80] = 0xAF;
        NoteTheStreamBeingReadied(fake);

        Refused(fake);

        Assert.NotNull(fake.BulkOpened);
        Assert.True(fake.BulkClosed);
        Assert.Equal(0x85000000u, fake.Word(0xFDBC04));
        Assert.Equal(0x000403FAu, fake.Word(0xFDBC14));
        Assert.Equal(0x00FF6000u, fake.Word(0xFDBD08));
        Assert.Equal(CopyProgram.UsbGroupAtRest, fake.Word(CopyProgram.UsbGroup));
        Assert.Equal(0x02000000u, fake.Word(0x01901020));
        Assert.Equal(0u, fake.Word(0x01901040));
        Assert.DoesNotContain("intake", fake.Log);
        Assert.NotEqual(CopyProgram.Running, fake.Word(CopyProgram.CaptureRunning));
    }

    [Fact]
    public void ADemodulatorThatDoesNotAnswerStopsTheOpeningBeforeTheHook()
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Acknowledging = false;

        DvbDeviceException refused = Refused(fake);

        Assert.Equal(TuningFailure.DeviceUnusable, refused.Failure);
        Assert.DoesNotContain(fake.WritesSoFar(), write => FakeDt295.AddressOf(write.Setup) == CopyProgram.HookAt);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void AnInstructionThatDoesNotComeBackStopsTheStream()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened();
        fake.InstructionsTimeOut = true;

        Assert.Equal(TuningFailure.DeviceUnusable, Assert.Throws<DvbDeviceException>(() => device.Read(188, CancellationToken.None)).Failure);
        device.Dispose();
    }

    [Fact]
    public void ARingThatGotTooFarAheadIsJumpedAndTheLossCounted()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened(broadcasting: false);
        fake.Accepting = false;
        fake.Broadcast((int)(Dt295Stream.Behind / CaptureUnpacker.UnitBytes) + UnitsPerTransfer);

        SpinWait.SpinUntil(() => device.Overflows > 0, TimeSpan.FromSeconds(30));

        Assert.True(device.Overflows > 0);
        device.Dispose();
    }

    [Fact]
    public void MoreThanASecondBetweenInstructionsIsTakenAsALoss()
    {
        (Dt295TunerDevice device, _, SteppedClock clock) = Opened();

        clock.Advance(TimeSpan.FromSeconds(2));
        SpinWait.SpinUntil(() => device.Overflows > 0, TimeSpan.FromSeconds(30));

        Assert.True(device.Overflows > 0);
        device.Dispose();
    }

    [Fact]
    public async Task ALockedTunerThatSendsNothingSaysSo()
    {
        (Dt295TunerDevice device, _, SteppedClock clock) = Opened(broadcasting: false);
        Task later = Task.Run(async () =>
        {
            await Task.Delay(200);
            clock.Advance(TimeSpan.FromSeconds(6));
        });

        Assert.Equal(TuningFailure.LockedWithoutData, Assert.Throws<DvbDeviceException>(() => device.Read(188, CancellationToken.None)).Failure);
        await later;
        device.Dispose();
    }

    [Fact]
    public void TheSignalIsMeasuredWhileStreaming()
    {
        (Dt295TunerDevice device, _, _) = Opened();

        SignalQuality quality = device.Measure();

        Assert.True(quality.HasLock);
        Assert.True(quality.CarrierToNoise.TryGetDecibels(out double decibels));
        Assert.Equal(38.0, decibels, 0.05);
        Assert.Equal(SignalReading.NotImplementedByThisTuner, quality.PostViterbiErrors.Reading);

        for (int again = 0; again < 20; again++)
        {
            Assert.True(device.Measure().HasLock);
        }

        Assert.NotEmpty(device.Read(TsPacketReader.PacketLength * 100, CancellationToken.None));
        device.Dispose();
    }

    [Fact]
    public void TheCardInTheSlotAnswersWhileTheStreamFlows()
    {
        (Dt295TunerDevice device, FakeDt295 fake, _) = Opened();

        using (ConditionalAccessCard card = ConditionalAccessCard.Open(device.CardService()))
        {
            Assert.True(card.Answer([0x01, 0x02, 0x03]).Granted);
        }

        Assert.True(fake.Instructions > 0);
        device.Dispose();
        Assert.Equal(0u, fake.Word(CardUart.Power));
    }
}

public sealed class TurnTakingConnectionTests
{
    [Fact]
    public void CallersAreServedInTheOrderTheyAsked()
    {
        using var first = new ManualResetEventSlim();
        var inner = new HoldingConnection(first);
        var turns = new TurnTakingConnection(inner);
        List<string> served = [];
        inner.Served = served;

        Thread holder = new(() => turns.ControlOut(new UsbSetup(0x40, 0xBC, 0, 1), [], TimeSpan.Zero));
        holder.Start();
        SpinWait.SpinUntil(() => inner.Holding);
        Thread second = new(() => turns.ControlOut(new UsbSetup(0x40, 0xBC, 0, 2), [], TimeSpan.Zero));
        second.Start();
        SpinWait.SpinUntil(() => second.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
        Thread third = new(() => turns.ControlOut(new UsbSetup(0x40, 0xBC, 0, 3), [], TimeSpan.Zero));
        third.Start();
        SpinWait.SpinUntil(() => third.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
        first.Set();
        holder.Join();
        second.Join();
        third.Join();

        Assert.Equal(["1", "2", "3"], served);
    }

    private sealed class HoldingConnection(ManualResetEventSlim release) : IUsbConnection
    {
        public List<string> Served { get; set; } = [];

        public volatile bool Holding;

        public string Path => "/dev/x";

        public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout) => 0;

        public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            if (setup.Index is 1)
            {
                Holding = true;
                release.Wait();
            }

            lock (Served)
            {
                Served.Add(setup.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) => throw new NotSupportedException();

        public void Dispose() { }
    }
}

public sealed class Dt295DetectionTests
{
    private static string Configuration(string kind, bool lnbPower = false) => $$"""
        {
          "socketPath": "/run/carina/driver.sock",
          "outputRoots": [{ "name": "primary", "path": "/srv/recordings" }],
          "tuner": { "backend": "usb" },
          "devices": [{ "id": "usb1-2", "kind": "{{kind}}", "devicePath": "/dev/bus/usb/001/005", "lnbPower": {{(lnbPower ? "true" : "false")}} }]
        }
        """;

    [Fact]
    public void TheTunerIsOfferedUnderItsNameWithBothKindsItReceives()
    {
        var host = new FakeDt295Host(FakeDt295.Locking());
        host.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/005", Dt295TunerDevice.Model, "1-2"));
        host.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/003", CechZd1TunerDevice.Model, "1-1"));
        host.Nodes.Add(new UsbDeviceNode("/dev/bus/usb/001/004", new UsbModel(0x06B8, 0x105C), "1-3"));

        IReadOnlyList<TunerDetection> found = UsbTunerDetector.Using(host).Detect();

        Assert.Equal(["usb1-2", "usb1-1"], found.Select(tuner => tuner.DeviceId));
        TunerDetection dt295 = found[0];
        Assert.Equal([DeviceKind.Terrestrial, DeviceKind.Satellite], dt295.Receives);
        Assert.Equal("PIXELA PIX-DT295", dt295.Hardware!.Name);
        Assert.Equal("/dev/bus/usb/001/005", UsbTunerDetector.NodeFor(host, "usb1-2", null));
    }

    [Fact]
    public void ASatelliteDeviceIsDeclaredOnTheUsbBackendLikeATerrestrialOne()
    {
        Assert.Empty(DriverConfigurationReader.Read(Configuration("satellite")).Problems);
    }

    [Fact]
    public void NoUsbTunerPowersALowNoiseBlock()
    {
        DriverConfigurationResult result = DriverConfigurationReader.Read(Configuration("satellite", lnbPower: true));

        Assert.Contains(result.Problems, problem => problem.StartsWith("devices[0].lnbPower:", StringComparison.Ordinal));
    }
}

public sealed class Dt295FactoryTests
{
    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, "/dev/bus/usb/001/005");

    private static TunerDeviceFactory Factory(FakeDt295 fake, IDescramblerFactory? descramblers = null)
    {
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        return TunerDeviceFactory.Using(
            new DriverConfiguration(
                OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                Tuner: new TunerSettings(TunerBackend.Usb),
                Devices: [Tuner]
            ),
            new FakeDt295Host(fake),
            pacing,
            descramblers,
            fake.HandlerHash()
        );
    }

    [Fact]
    public void WithoutAnotherCardTheTunerIsReadThroughTheCardInItsOwnSlot()
    {
        FakeDt295 fake = FakeDt295.Locking();

        using ITunerDevice device = Factory(fake).Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 22), null, HeadHold.EveryProgramme);

        Assert.IsType<DescramblingTunerDevice>(device);
        Assert.Equal([0x90, 0x30, 0x00, 0x00, 0x00], fake.Card.Commands[0]);
    }

    [Fact]
    public void CheckingATunerNothingKeepsStartsItAfreshAndKeepsItReady()
    {
        FakeDt295 fake = FakeDt295.Locking();
        TunerDeviceFactory factory = Factory(fake);

        factory.Check(Tuner);

        Assert.True(CopyProgram.IsOurHook(fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)));
        Assert.False(fake.Disposed);
        factory.Dispose();
        Assert.True(fake.Disposed);
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
    }

    [Fact]
    public void TheSonyTunerStillRefusesASatelliteTune()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        FakeUsbHost host = new(UsbTestSupport.BridgeWith(demodulator, tuner));
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        DeviceSettings satellite = new("usb1-1", DeviceKind.Satellite, "/dev/bus/usb/001/002");
        TunerDeviceFactory factory = TunerDeviceFactory.Using(
            new DriverConfiguration(
                OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                Tuner: new TunerSettings(TunerBackend.Usb),
                Devices: [satellite]
            ),
            host,
            pacing
        );

        Assert.Throws<DvbDeviceException>(() =>
            factory.Create(satellite, new TuningRequest(TunerKind.Satellite, 2), null, HeadHold.EveryProgramme)
        );
        Assert.Equal(0, host.Opened);
    }
}
