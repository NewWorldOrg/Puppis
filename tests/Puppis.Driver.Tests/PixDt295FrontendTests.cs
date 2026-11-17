using Puppis.Contracts;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class Dt295TerrestrialTests
{
    private const string Channel27 = """
        RT 0B 01
        RT 12 01
        wait 20
        T 03 00
        T 1D AA
        T 1E AA
        T 1F AA
        T 25 00
        T 23 4D
        RTM 0D 00
        RT 0F 20
        RT 10 49
        RT 11 8B
        RT 00 01
        RT 31 81
        RT 00 00
        RT 7C 1F
        RT 12 00
        RTM 6E 80
        RTM 14 00
        RT 12 01
        wait 20
        RTM 6E 00
        TM 0E 71
        T 0F 10
        T 01 40
        T 23 4C
        wait 100
        T 1D 02
        T 1E 2A
        T 1F 8A
        wait 100
        TR 80
        TR B0
        """;

    private static (Dt295Frontend Frontend, FakeDt295 Device, SteppedClock Clock) Frontend()
    {
        FakeDt295 device = FakeDt295.Locking();
        (DevicePacing pacing, SteppedClock clock) = PixDt295Support.Pacing(device);

        return (new Dt295Frontend(PixDt295Support.Chips(device), pacing), device, clock);
    }

    [Fact]
    public void TheTunerIsGivenSixtyFourTimesTheFrequencyInMegahertz()
    {
        Assert.Equal(35_657, Dt295Frontend.TerrestrialDivider(BroadcastStandards.TerrestrialCentreHz(27)));
        Assert.Equal(557_142_857, BroadcastStandards.TerrestrialCentreHz(27));
    }

    [Fact]
    public void TuningFollowsTheTableAndLooksForTheLockAfterATenthOfASecond()
    {
        (Dt295Frontend frontend, FakeDt295 device, _) = Frontend();

        bool locked = frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(27));

        Assert.True(locked);
        Assert.Equal(PixDt295Support.Expected(Channel27), PixDt295Support.Written(device));
        Assert.Equal(Dt295Side.Terrestrial, frontend.Tuned);
    }

    [Fact]
    public void AChannelThatDoesNotLockIsGivenUpOnAfterThreeSeconds()
    {
        (Dt295Frontend frontend, FakeDt295 device, SteppedClock clock) = Frontend();
        device.Demodulator[0x80] = 0xAF;
        device.Demodulator[0xB0] = 0xA0;
        DateTimeOffset started = clock.GetUtcNow();

        bool locked = frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(60));

        Assert.False(locked);
        Assert.Equal(TimeSpan.FromMilliseconds(140) + TimeSpan.FromSeconds(3), clock.GetUtcNow() - started);
        Assert.Equal(30, device.Transfers.Count(transfer => transfer.Written is [0x80]));
    }

    [Theory]
    [InlineData(new byte[] { 0x06, 0x10, 0x63, 0x00 }, 0xA9, true)]
    [InlineData(new byte[] { 0x07, 0x10, 0x78, 0x00 }, 0xA9, true)]
    [InlineData(new byte[] { 0xAF, 0x10, 0xC1, 0x00 }, 0xA0, false)]
    [InlineData(new byte[] { 0x2E, 0x00, 0x00, 0x00 }, 0xA0, false)]
    [InlineData(new byte[] { 0x26, 0x10, 0x63, 0x00 }, 0xA9, false)]
    [InlineData(new byte[] { 0x0E, 0x10, 0x63, 0x00 }, 0xA9, false)]
    [InlineData(new byte[] { 0x06, 0x10, 0x63, 0x00 }, 0xA7, false)]
    public void LockIsBitsThreeAndFiveClearWithEnoughLayers(byte[] status, byte layers, bool locked)
    {
        Assert.Equal(locked, Dt295Signal.TerrestrialLock(status, layers));
    }

    [Theory]
    [InlineData(0x000756, 38.0)]
    [InlineData(0x00133F, 32.2)]
    [InlineData(0x005144, 25.3)]
    [InlineData(0x028AE7, 16.3)]
    public void TheCarrierToNoiseRatioIsReadOffTheCurve(int raw, double decibels)
    {
        double? found = Dt295Signal.TerrestrialDecibels([(byte)(raw >> 16), (byte)(raw >> 8), (byte)raw]);

        Assert.NotNull(found);
        Assert.Equal(decibels, found.Value, 0.05);
    }

    [Fact]
    public void NothingMeasuredIsNoReading()
    {
        Assert.Null(Dt295Signal.TerrestrialDecibels([0, 0, 0]));
    }

    [Fact]
    public void TheRatioIsReadOnlyWhileLocked()
    {
        (Dt295Frontend frontend, FakeDt295 device, _) = Frontend();
        frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(22));

        Assert.True(frontend.CarrierToNoise(true).TryGetDecibels(out double decibels));
        Assert.Equal(38.0, decibels, 0.05);
        Assert.Equal(CarrierToNoise.WithoutLock, frontend.CarrierToNoise(false));
        device.Demodulator[0x8C] = 0;
        device.Demodulator[0x8D] = 0;
        Assert.Equal(CarrierToNoise.Unavailable, frontend.CarrierToNoise(true));
    }

    [Fact]
    public void TheDemodulatorAndTheTunerMustNameThemselves()
    {
        (Dt295Frontend frontend, FakeDt295 device, _) = Frontend();
        frontend.Identify("/dev/x");
        Assert.Equal(["20 w[0E]", "20 w[FEC0FB18]", "20 w[FEC1]"], PixDt295Support.Written(device));

        device.TerrestrialTuner[0x18] = 0x04;
        Assert.Equal(TuningFailure.DeviceUnusable, Assert.Throws<DvbDeviceException>(() => frontend.Identify("/dev/x")).Failure);
        device.Demodulator[0x0E] = 0x2E;
        Assert.Equal(TuningFailure.DeviceUnusable, Assert.Throws<DvbDeviceException>(() => frontend.Identify("/dev/x")).Failure);
    }

    [Fact]
    public void ADemodulatorThatDoesNotAcknowledgeLeavesTheBusReleased()
    {
        (Dt295Frontend frontend, FakeDt295 device, _) = Frontend();
        device.Acknowledging = false;

        DvbDeviceException failed = Assert.Throws<DvbDeviceException>(() => frontend.Identify("/dev/x"));

        Assert.Equal(TuningFailure.DeviceUnusable, failed.Failure);
        Assert.Equal(0x40u | 0x01u, device.Word(0xFD8FE4));
    }
}

/// <summary>
/// The satellite side follows the specification only. None of it has been through a real satellite signal.
/// </summary>
public sealed class Dt295SatelliteFollowsTheSpecOnlyTests
{
    private const string BsOneOnStream = """
        SM 1E 20
        RS 11 00 A7
        RS 2A B9
        RS 1C C0
        RS 29 71
        S 17 00
        S 1E 20
        S 0A FF
        S 03 01
        S 1C AA
        S 1F 2A
        S 8F 00
        S 90 00
        S 1E 28
        S 0A 00
        S 03 01
        RS 00 10 64 00 00 80
        RS 09 58
        RS 37 AC
        RS 05 80
        RS 2A BB
        wait 10
        T 0E 77
        T 0F 77
        SM 07 01
        S 08 10
        RSR
        RS 50 F1
        wait 40
        S 1E 20
        S 0A FF
        S 03 01
        S 03 01
        wait 100
        S 1C 08
        S 1F 08
        SM A5 40
        SM A3 11
        S 8F 40
        S 90 10
        wait 100
        SR C3
        SR E6
        """;

    private static (Dt295Frontend Frontend, FakeDt295 Device) Frontend()
    {
        FakeDt295 device = FakeDt295.Locking();
        (DevicePacing pacing, _) = PixDt295Support.Pacing(device);

        return (new Dt295Frontend(PixDt295Support.Chips(device), pacing), device);
    }

    [Fact]
    public void TheLowBandDoublesTheDividerAndSetsItsFlag()
    {
        SatellitePlan plan = SatellitePlan.For(BroadcastStandards.BsCentreKHz(1), "/dev/x");

        Assert.Equal([0x10, 0x64, 0x00, 0x00, 0x80], plan.Pll);
        Assert.Equal(0x58, plan.Band);
    }

    [Fact]
    public void AboveTheLowBandTheDividerIsTheFrequencyInMegahertz()
    {
        SatellitePlan plan = SatellitePlan.For(BroadcastStandards.Cs110CentreKHz(2), "/dev/x");

        Assert.Equal([0x0C, 0x9A, 0x00, 0x00, 0x00], plan.Pll);
        Assert.Equal(0x44, plan.Band);
    }

    [Fact]
    public void TuningFollowsTheTableAndLocksOnTheStreamAsked()
    {
        (Dt295Frontend frontend, FakeDt295 device) = Frontend();
        device.SatelliteTunerReads.Enqueue(0x00);
        device.SatelliteTunerReads.Enqueue(0x60);
        device.Satellite[0xE6] = 0x40;
        device.Satellite[0xE7] = 0x10;

        bool locked = frontend.TuneSatellite(BroadcastStandards.BsCentreKHz(1), 0x4010, "/dev/x");

        Assert.True(locked);
        Assert.Equal(PixDt295Support.Expected(BsOneOnStream), PixDt295Support.Written(device));
    }

    [Fact]
    public void AnotherStreamOnTheTransponderIsNotALock()
    {
        (Dt295Frontend frontend, FakeDt295 device) = Frontend();
        device.Satellite[0xE6] = 0x40;
        device.Satellite[0xE7] = 0x11;

        Assert.False(frontend.TuneSatellite(BroadcastStandards.BsCentreKHz(1), 0x4010, "/dev/x"));
    }

    [Fact]
    public void WithoutAStreamNamedTheFirstOneOfferedIsTaken()
    {
        (Dt295Frontend frontend, FakeDt295 device) = Frontend();
        device.Satellite[0xCE] = 0x00;
        device.Satellite[0xCF] = 0x00;
        device.Satellite[0xD0] = 0x60;
        device.Satellite[0xD1] = 0x20;
        device.Satellite[0xE6] = 0x60;
        device.Satellite[0xE7] = 0x20;

        Assert.True(frontend.TuneSatellite(BroadcastStandards.Cs110CentreKHz(2), null, "/dev/x"));
        Assert.Equal(0x60, device.Satellite[0x8F]);
        Assert.Equal(0x20, device.Satellite[0x90]);
    }

    [Fact]
    public void GoingFromOneSideToTheOtherPutsTheFirstToRest()
    {
        (Dt295Frontend frontend, FakeDt295 device) = Frontend();
        device.Satellite[0xE6] = 0x40;
        device.Satellite[0xE7] = 0x10;
        frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(22));
        device.Log.Clear();

        frontend.TuneSatellite(BroadcastStandards.BsCentreKHz(1), 0x4010, "/dev/x");

        Assert.Equal(PixDt295Support.Expected("T 25 00\nT 23 4D\nT 03 90\nRT 12 00\nRT 0B 00"), PixDt295Support.Written(device).Take(5));
        device.Log.Clear();

        frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(22));

        Assert.Equal(
            PixDt295Support.Expected("S 1C AA\nS 1F 2A\nS 1E 28\nS 0A 00\nS 03 01\nS 17 01\nSM 1E 38\nRS 2A B9\nRS 29 70\nRS 28 3E\nRS 2A 19\nRS 1C 00\nRS 11 C0 A7"),
            PixDt295Support.Written(device).Take(14)
        );
    }

    [Theory]
    [InlineData(2999, 0.0)]
    [InlineData(3000, 58.857)]
    public void TheSatelliteRatioStartsAtItsFloor(int raw, double decibels)
    {
        Assert.Equal(decibels, Dt295Signal.SatelliteDecibels([(byte)(raw >> 8), (byte)raw]), 0.001);
    }

    [Fact]
    public void ASatelliteLockIsBitFourClear()
    {
        Assert.True(Dt295Signal.SatelliteLock(0xEF));
        Assert.False(Dt295Signal.SatelliteLock(0x10));
    }
}
