using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class ReceptionProbeTests
{
    private const string UsbPath = "/dev/bus/usb/001/005";

    private const string Frontend = "/dev/dvb/adapter0/frontend0";

    private const byte NotLocked = 0xAF;

    private const FrontendStatus Locked =
        FrontendStatus.Signal
        | FrontendStatus.Carrier
        | FrontendStatus.Viterbi
        | FrontendStatus.Sync
        | FrontendStatus.Lock;

    private static readonly DeviceSettings UsbTuner = new("usb1-2", DeviceKind.Terrestrial, UsbPath);

    private static readonly DeviceSettings DvbTuner = new("card-0", DeviceKind.Terrestrial, Frontend);

    private static readonly DeviceSettings DvbDish = new("card-1", DeviceKind.Satellite, Frontend, LnbPower: true);

    private static readonly TuneParams Channel = TuneParams.Terrestrial(22);

    private static DriverConfiguration Configuration(TunerBackend backend, params DeviceSettings[] devices) =>
        new(
            OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
            Tuner: new TunerSettings(backend),
            Devices: devices
        );

    private static (TunerDeviceFactory Factory, ScriptedDvbSystemCalls Calls) Dvb(FrontendStatus status = Locked)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = new ScriptedDvbSystemCalls(clock);
        calls.ReportStatus(status);

        return (TunerDeviceFactory.Using(Configuration(TunerBackend.Dvb, DvbTuner, DvbDish), clock, calls), calls);
    }

    private static (TunerDeviceFactory Factory, FakeDt295 Fake) Dt295(bool locking = true)
    {
        FakeDt295 fake = FakeDt295.Locking();
        fake.Demodulator[0x80] = locking ? fake.Demodulator[0x80] : NotLocked;
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);

        return (
            TunerDeviceFactory.Using(
                Configuration(TunerBackend.Usb, UsbTuner),
                new FakeDt295Host(fake),
                pacing,
                null,
                fake.HandlerHash()
            ),
            fake
        );
    }

    private static (TunerDeviceFactory Factory, FakeBridge Bridge, FakeChip Demodulator) CechZd1()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        FakeBridge bridge = UsbTestSupport.BridgeWith(demodulator, tuner);
        (DevicePacing pacing, ManualTimeProvider clock, _) = UsbTestSupport.Pacing();
        bridge.Idle = clock.Advance;

        return (
            TunerDeviceFactory.Using(Configuration(TunerBackend.Usb, UsbTuner), new FakeUsbHost(bridge), pacing),
            bridge,
            demodulator
        );
    }

    private static void UseOnce(TunerDeviceFactory factory) =>
        factory.Create(UsbTuner, new TuningRequest(TunerKind.Terrestrial, 27), null, HeadHold.EveryProgramme).Dispose();

    [Fact]
    public void ASyntheticTunerAnswersWithALockedSignal()
    {
        using var factory = new TunerDeviceFactory(Configuration(TunerBackend.Fake, UsbTuner), TimeProvider.System);

        using IReceptionProbe probe = factory.Tune(UsbTuner, Channel, CancellationToken.None);

        Assert.True(probe.Measure().HasLock);
        Assert.True(probe.Measure().CarrierToNoise.TryGetDecibels(out _));
    }

    [Fact]
    public void ADvbTunerIsMeasuredThroughItsFrontendAloneAndClosedAgain()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls) = Dvb();
        calls.AnswerWith(DvbProperty.CarrierToNoise, [new DvbStatisticLayer(StatisticScale.Decibel, 23_400)]);

        IReceptionProbe probe = factory.Tune(DvbTuner, Channel, CancellationToken.None);
        SignalQuality quality = probe.Measure();

        Assert.Equal(new OpenedNode(Frontend, DvbAccess.Control, calls.Opened[0].Descriptor), Assert.Single(calls.Opened));
        Assert.Single(calls.PropertiesSet);
        Assert.Empty(calls.FiltersSet);
        Assert.Empty(calls.BufferSizesSet);
        Assert.Equal(0, calls.ReadsMade);
        Assert.True(quality.HasLock);
        Assert.True(quality.CarrierToNoise.TryGetDecibels(out double decibels));
        Assert.Equal(23.4, decibels);
        Assert.Empty(calls.Closed);

        probe.Dispose();

        Assert.Equal([calls.Opened[0].Descriptor], calls.Closed);
    }

    [Fact]
    public void ADvbTunerThatDoesNotLockSaysSoAndIsClosed()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls) = Dvb(FrontendStatus.Signal);

        DvbDeviceException failed = Assert.Throws<DvbDeviceException>(
            () => factory.Tune(DvbTuner, Channel, CancellationToken.None)
        );

        Assert.Equal(TuningFailure.NoLock, failed.Failure);
        Assert.Equal(DvbTunerSettings.Default.LockPatience, calls.RestedFor);
        Assert.Equal([calls.Opened[0].Descriptor], calls.Closed);
    }

    [Fact]
    public void WaitingForADvbTunerToLockStopsAsSoonAsItIsToldTo()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls) = Dvb(FrontendStatus.Signal);
        using var stopping = new CancellationTokenSource();
        calls.Resting = stopping.Cancel;

        Assert.Throws<OperationCanceledException>(() => factory.Tune(DvbTuner, Channel, stopping.Token));

        Assert.Equal(DvbTunerSettings.Default.LockPollInterval, calls.RestedFor);
        Assert.Equal([calls.Opened[0].Descriptor], calls.Closed);
    }

    [Fact]
    public void ADishIsFedAsTheLedgerSaysWhileItIsMeasured()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls) = Dvb();

        factory.Tune(DvbDish, TuneParams.Cs110(2), CancellationToken.None).Dispose();

        Assert.Equal([LnbVoltage.Eighteen], calls.VoltagesSet);
    }

    [Fact]
    public void ADt295ThatWasClosedIsMeasuredWithoutAStreamOrTheCardAndClosedAgain()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Dt295();

        IReceptionProbe probe = factory.Tune(UsbTuner, Channel, CancellationToken.None);
        SignalQuality quality = probe.Measure();

        Assert.True(quality.HasLock);
        Assert.Null(fake.BulkOpened);
        Assert.Equal(0u, fake.Word(CardUart.Power));
        Assert.Empty(fake.Card.Commands);
        Assert.False(fake.Disposed);

        probe.Dispose();

        Assert.True(fake.Disposed);
        Assert.Equal(CopyProgram.HookAtPowerOn, [fake.Word(CopyProgram.HookAt), fake.Word(CopyProgram.HookAt + 4)]);
        Assert.False(factory.IsKeptOpen(UsbTuner));
    }

    [Fact]
    public void ADt295ThatWasKeptOpenIsStillKeptOpenAfterItIsMeasured()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Dt295();
        UseOnce(factory);
        int commands = fake.Card.Commands.Count;

        IReceptionProbe probe = factory.Tune(UsbTuner, Channel, CancellationToken.None);

        Assert.False(factory.IsKeptOpen(UsbTuner));
        Assert.True(probe.Measure().HasLock);

        probe.Dispose();

        Assert.True(factory.IsKeptOpen(UsbTuner));
        Assert.False(fake.Disposed);
        Assert.True(fake.BulkClosed);
        Assert.Equal(commands, fake.Card.Commands.Count);
        Assert.Equal(0u, fake.Word(CardUart.Power));
    }

    [Fact]
    public void ADt295ThatDoesNotLockIsLeftAsItWasFound()
    {
        (TunerDeviceFactory closed, FakeDt295 closedFake) = Dt295(locking: false);
        (TunerDeviceFactory kept, FakeDt295 keptFake) = Dt295();
        UseOnce(kept);
        keptFake.Demodulator[0x80] = NotLocked;

        DvbDeviceException whenClosed = Assert.Throws<DvbDeviceException>(
            () => closed.Tune(UsbTuner, Channel, CancellationToken.None)
        );
        DvbDeviceException whenKept = Assert.Throws<DvbDeviceException>(
            () => kept.Tune(UsbTuner, Channel, CancellationToken.None)
        );

        Assert.Equal(TuningFailure.NoLock, whenClosed.Failure);
        Assert.Equal(TuningFailure.NoLock, whenKept.Failure);
        Assert.True(closedFake.Disposed);
        Assert.False(closed.IsKeptOpen(UsbTuner));
        Assert.False(keptFake.Disposed);
        Assert.True(kept.IsKeptOpen(UsbTuner));
    }

    [Fact]
    public void ADt295DeclaredTerrestrialIsNotMeasuredOnASatelliteChannel()
    {
        (TunerDeviceFactory factory, FakeDt295 fake) = Dt295();

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(
            () => factory.Tune(UsbTuner, TuneParams.Cs110(2), CancellationToken.None)
        );

        Assert.Equal(TuningFailure.Unspecified, refused.Failure);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public void ACechZd1ThatWasClosedIsMeasuredWithoutAStreamAndClosedAgain()
    {
        (TunerDeviceFactory factory, FakeBridge bridge, _) = CechZd1();

        IReceptionProbe probe = factory.Tune(UsbTuner, Channel, CancellationToken.None);
        SignalQuality quality = probe.Measure();

        Assert.True(quality.HasLock);
        Assert.True(quality.CarrierToNoise.TryGetDecibels(out _));
        Assert.Null(bridge.BulkOpened);
        Assert.Empty(bridge.WritesAt(0xD01040));
        Assert.False(bridge.Disposed);

        probe.Dispose();

        Assert.True(bridge.Disposed);
        Assert.False(factory.IsKeptOpen(UsbTuner));
    }

    [Fact]
    public void ACechZd1ThatWasKeptAsleepIsWokenMeasuredAndKeptAsleepAgain()
    {
        (TunerDeviceFactory factory, FakeBridge bridge, FakeChip demodulator) = CechZd1();
        UseOnce(factory);
        int bridgeStarts = bridge.WritesAt(0xF1010C).Count;

        IReceptionProbe probe = factory.Tune(UsbTuner, Channel, CancellationToken.None);

        Assert.False(factory.IsKeptOpen(UsbTuner));
        Assert.True(probe.Measure().HasLock);

        probe.Dispose();

        Assert.True(factory.IsKeptOpen(UsbTuner));
        Assert.False(bridge.Disposed);
        Assert.Equal(bridgeStarts, bridge.WritesAt(0xF1010C).Count);
        Assert.Equal(0, demodulator.Last(1286));
    }

    [Fact]
    public void ACechZd1ThatDoesNotLockSaysSoAndIsClosedWhenItWasClosed()
    {
        (TunerDeviceFactory factory, FakeBridge bridge, FakeChip demodulator) = CechZd1();
        demodulator.Answer(1284, 0x0001);

        DvbDeviceException failed = Assert.Throws<DvbDeviceException>(
            () => factory.Tune(UsbTuner, Channel, CancellationToken.None)
        );

        Assert.Equal(TuningFailure.NoLock, failed.Failure);
        Assert.True(bridge.Disposed);
        Assert.False(factory.IsKeptOpen(UsbTuner));
    }

    [Fact]
    public void ACechZd1ThatWasKeptAndDoesNotLockIsStartedAfreshOnceAndKeptAgain()
    {
        (TunerDeviceFactory factory, FakeBridge bridge, FakeChip demodulator) = CechZd1();
        UseOnce(factory);
        int bridgeStarts = bridge.WritesAt(0xF1010C).Count;
        demodulator.Answer(1284, 0x0001);

        DvbDeviceException failed = Assert.Throws<DvbDeviceException>(
            () => factory.Tune(UsbTuner, Channel, CancellationToken.None)
        );

        Assert.Equal(TuningFailure.NoLock, failed.Failure);
        Assert.Equal(bridgeStarts + 1, bridge.WritesAt(0xF1010C).Count);
        Assert.True(factory.IsKeptOpen(UsbTuner));
    }

    [Fact]
    public void ACechZd1IsNotMeasuredOnASatelliteChannel()
    {
        (TunerDeviceFactory factory, FakeBridge bridge, _) = CechZd1();

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(
            () => factory.Tune(UsbTuner with { Kind = DeviceKind.Satellite }, TuneParams.Cs110(2), CancellationToken.None)
        );

        Assert.Equal(TuningFailure.Unspecified, refused.Failure);
        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void AProbeIsClosedOnlyOnce()
    {
        (TunerDeviceFactory dt295, FakeDt295 fake) = Dt295();
        (TunerDeviceFactory cechZd1, FakeBridge bridge, _) = CechZd1();
        IReceptionProbe first = dt295.Tune(UsbTuner, Channel, CancellationToken.None);
        IReceptionProbe second = cechZd1.Tune(UsbTuner, Channel, CancellationToken.None);

        first.Dispose();
        long requests = fake.Requests;
        first.Dispose();
        second.Dispose();
        int written = bridge.Writes.Count;
        second.Dispose();

        Assert.Equal(requests, fake.Requests);
        Assert.Equal(written, bridge.Writes.Count);
        Assert.Throws<ObjectDisposedException>(() => first.Measure());
        Assert.Throws<ObjectDisposedException>(() => second.Measure());
    }
}
