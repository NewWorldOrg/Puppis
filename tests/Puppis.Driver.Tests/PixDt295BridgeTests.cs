using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class BridgeWindowTests
{
    [Fact]
    public void AnAddressAboveTwentyFourBitsIsReadThroughTheWindowWhichIsThenPutBack()
    {
        FakeDt295 device = FakeDt295.Locking();
        device.Word(0x01902028, 0x13D1C0);
        var window = new BridgeWindow(new BridgeMemory(new TurnTakingConnection(device)));

        uint found = window.Read(0x01902028);

        Assert.Equal(0x13D1C0u, found);
        Assert.Equal([0x01900000u, BridgeWindow.AtPowerOn], device.WordsWrittenTo(BridgeWindow.ReadWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, device.Word(BridgeWindow.ReadWindow));
        Assert.Empty(device.WordsWrittenTo(BridgeWindow.WriteWindow));
    }

    [Fact]
    public void AWriteGoesThroughTheWriteWindow()
    {
        FakeDt295 device = FakeDt295.Locking();
        var window = new BridgeWindow(new BridgeMemory(new TurnTakingConnection(device)));

        window.WriteAndCheck(0x01901044, 0x00BC0000);

        Assert.Equal(0x00BC0000u, device.Word(0x01901044));
        Assert.Equal([0x01900000u, BridgeWindow.AtPowerOn], device.WordsWrittenTo(BridgeWindow.WriteWindow));
        Assert.Equal(BridgeWindow.AtPowerOn, device.Word(BridgeWindow.WriteWindow));
    }

    [Fact]
    public void AWindowSomethingElseHasMovedAsksForTheTunerToBeReplugged()
    {
        FakeDt295 device = FakeDt295.Locking();
        device.Word(BridgeWindow.ReadWindow, 0x01600000);
        var window = new BridgeWindow(new BridgeMemory(new TurnTakingConnection(device)));

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() => window.Read(0x01902028));

        Assert.Equal(TuningFailure.DeviceUnusable, refused.Failure);
        Assert.Contains("plug it back in", refused.Message, StringComparison.Ordinal);
        Assert.Empty(device.WordsWrittenTo(BridgeWindow.ReadWindow));
    }

    [Fact]
    public void AReadThatWouldCrossOutOfItsRegionIsRefused()
    {
        var window = new BridgeWindow(new BridgeMemory(new TurnTakingConnection(FakeDt295.Locking())));

        Assert.Throws<ArgumentOutOfRangeException>(() => window.ReadWords(0x0191FFF8, 4));
    }
}

public sealed class Dt295StartupTests
{
    [Fact]
    public void ABridgeSomethingElseHasSetUpIsLeftAloneAndTheTunerAskedToBeReplugged()
    {
        FakeDt295 device = FakeDt295.Locking();
        device.Word(0xF02008, 0x00010000);

        var memory = new BridgeMemory(new TurnTakingConnection(device));

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() => Dt295Startup.RequirePowerOnState(memory));

        Assert.Contains("plug it back in", refused.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void TheHandlerIsCheckedWithTheHookCountedAsItIsAtPowerOn()
    {
        FakeDt295 device = FakeDt295.Locking();
        string known = device.HandlerHash();
        var memory = new BridgeMemory(new TurnTakingConnection(device));
        Dt295Startup.RequireTheKnownHandler(memory, known);

        device.Word(CopyProgram.HookAt, CopyProgram.Hook[0]);
        device.Word(CopyProgram.HookAt + 4, CopyProgram.Hook[1]);
        Dt295Startup.RequireTheKnownHandler(memory, known);

        device.Word(0xE9100, 0x12345678);
        Assert.Throws<DvbDeviceException>(() => Dt295Startup.RequireTheKnownHandler(memory, known));
    }

    [Fact]
    public void TheDemodulatorIsHeldInResetAndReleased()
    {
        FakeDt295 device = FakeDt295.Locking();
        List<string> seen = [];
        var pacing = new DevicePacing(TimeProvider.System, span => seen.Add($"wait {span.TotalMilliseconds:0}"));
        var memory = new RecordingConnection(device, seen);

        Dt295Startup.ResetTheDemodulator(new BridgeMemory(new TurnTakingConnection(memory)), pacing);

        Assert.Equal(["F10014 0002", "F10018 0000", "wait 20", "F10018 0002", "wait 5"], seen);
    }

    [Fact]
    public void TheClocksAndTheI2cMasterAreStartedAsTheTablesSay()
    {
        FakeDt295 device = FakeDt295.Locking();
        device.Word(0xF1011C, 0xFFFF0000);
        var memory = new BridgeMemory(new TurnTakingConnection(device));

        Dt295Startup.StartClocks(memory);
        Dt295Startup.StartI2cMaster(memory);

        Assert.Equal(0xFFFF0000u, device.Word(0xF10118));
        Assert.Equal(0xFF9F0000u, device.Word(0xF1011C));
        Assert.Equal(0x1A000000u, device.Word(0xF001F0));
        Assert.Equal(0x80200000u, device.Word(0xF10114));
        Assert.Equal([1u, 0u], device.WordsWrittenTo(0xFD80C0));
        Assert.Equal(0x10u, device.Word(0xFD8FF4));
        Assert.Equal(0x06u, device.Word(0xFD8FF8));
        Assert.Equal(0x3Cu, device.Word(0xFD8FE8));
        Assert.Equal(0x40u | BridgeI2c.ControlByteDone, device.Word(0xFD8FE4));
        Assert.Equal(
            [0xF10118u, 0xF1011Cu, 0xF001F0u, 0xF10114u, 0xFD8010u, 0xFD8014u, 0xFD80C0u, 0xFD80C0u, 0xFD80C4u, 0xFD8FF8u, 0xFD8FF4u, 0xFD8FE8u, 0xFD8FE4u],
            device.Writes.Select(write => FakeDt295.AddressOf(write.Setup))
        );
    }
}

internal sealed class RecordingConnection(IUsbConnection usb, List<string> seen) : IUsbConnection
{
    public string Path => usb.Path;

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout) => usb.ControlIn(setup, data, timeout);

    public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        seen.Add($"{FakeDt295.AddressOf(setup):X6} {Convert.ToHexString(data)}");
        usb.ControlOut(setup, data, timeout);
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight) =>
        usb.ReadBulk(endpoint, transferBytes, transfersInFlight);

    public void Dispose() => usb.Dispose();
}

public sealed class Dt295InitialisationTests
{
    private const string Table = """
        wait 20
        wait 5
        T 01 00
        T 03 90
        T 06 80
        T 1C 20
        T 1D 56
        T 1E 6A
        T 1F 9A
        T 23 4D
        T 25 00
        T 26 00
        T 32 21
        T 3B 21
        T 3C 3F
        T 73 45
        T 74 11
        T 75 02
        T 76 00
        T 77 01
        T 7D D2
        T E4 F4
        T F4 80
        RT FF
        RT 00 01
        RT 96 00
        RT 00 00
        RT 14 13
        RT 6D 8A
        RT 6D 0A
        RT DF 19
        RT 45 1B
        RT A9 59
        RT AA 6A
        RT BE 4C
        RT CF 2A
        RT D2 F5
        RT D0 34
        RT 77 E7
        RT 78 E3
        RT 6F 51
        RT 7B 84
        RT 7C 9F
        RT 56 41
        RT CD 64
        RT C3 2C
        RT 9D 61
        RT F7 52
        RT 58 81
        RT B1 30
        RT 00 01
        RTM 31 D0
        RT 00 00
        RT 01 89
        RT 02 00
        RT 6D 0A
        RT 0B 01
        RTM 04 01
        RT 05 1B
        RTM 08 05
        RTM 09 42
        RTM 5E 00
        RT 0A 00
        RT 0C 00
        RT 13 04
        RT 53 FE
        RT 57 91
        RT 5A F9
        RT 5B 18
        RT 5C F1
        RT 62 C2
        RT 6E 01
        RT 6F 51
        RT 87 77
        RT 88 55
        RT 93 22
        RT 97 02
        RT BA 30
        RT 98 AF
        RT 9B 20
        RT 9C 1E
        RT A0 18
        RT A5 09
        RT C2 A9
        RT C5 7C
        RT CD E8
        RT CE 7F
        RT D5 03
        RT D9 04
        RT EA 0D
        RT DC 1C
        RT 6D 0A
        RT 03 00
        RT 03 01
        wait 100
        RTM 0D 00
        RT 0F 20
        RT 10 49
        RT 11 76
        RT 00 01
        RT 31 81
        RT 00 00
        RT 7C 1F
        RT 0B 01
        RT 12 01
        wait 20
        RT 12 00
        RT 0B 00
        T 25 00
        T 23 4D
        T 03 90
        S 01 90
        S 03 00
        S 04 02
        S 07 33
        S 08 11
        S 10 B0
        S 11 00
        S 13 80
        S 15 00
        S 17 01
        S 1C 59
        S 1D AA
        S 1E 20
        S 1F 19
        S 85 7A
        S 87 04
        S 8E 26
        S A3 91
        S A5 40
        wait 4
        RS 2A B9
        RS 06 10 10 00
        RS 0A 00
        RS 0E 00
        RS 2A B9
        RS 29 70
        RS 28 3E
        RS 2A 19
        RS 1C 00
        RS 11 C0 A7
        wait 6
        """;

    [Fact]
    public void TheChipsAreSetUpInTheOrderAndWithTheValuesOfTheTable()
    {
        FakeDt295 device = FakeDt295.Locking();
        (DevicePacing pacing, _) = PixDt295Support.Pacing(device);
        var frontend = new Dt295Frontend(PixDt295Support.Chips(device), pacing);

        frontend.Initialise();

        Assert.Equal(PixDt295Support.Expected(Table), PixDt295Support.Written(device));
    }

    [Fact]
    public void TheDemodulatorsSeventyIsLeftAsTheResetLeftIt()
    {
        FakeDt295 device = FakeDt295.Locking();
        (DevicePacing pacing, _) = PixDt295Support.Pacing(device);

        new Dt295Frontend(PixDt295Support.Chips(device), pacing).Initialise();

        Assert.DoesNotContain(device.Transfers, transfer => transfer.Address is Dt295Chips.Terrestrial && transfer.Written[0] is 0x70);
    }
}
