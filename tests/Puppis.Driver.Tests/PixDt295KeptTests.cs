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

/// <summary>
/// A PIX-DT295 stays open and ready between sessions, and is given back as it was found when the driver lets go of it.
/// </summary>
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
        Assert.Equal(1u, fake.Word(CardUart.Power));
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
    public void ATunerThePatrolFindsGoneIsLetGoOf()
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
            gone => letGo.Add(gone.Id!)
        );

        patrol.Walk();

        Assert.Equal(["usb1-2"], letGo);
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
