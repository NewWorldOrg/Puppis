using Puppis.Contracts;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class CechZd1WakeTests
{
    private const string Device = "/dev/bus/usb/001/002";

    private const int SixthBaseband = 0x06;

    private const int SeventhBaseband = 0x07;

    private const int DcSwitch = 0x1F;

    private const int SearchSequence = 355;

    private static readonly long Channel22 = BroadcastStandards.TerrestrialCentreHz(22);

    private static readonly long Channel27 = BroadcastStandards.TerrestrialCentreHz(27);

    private static (CechZd1Frontend Frontend, FakeChip Tuner, ManualTimeProvider Clock) Initialised()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        (DevicePacing pacing, ManualTimeProvider clock, _) = UsbTestSupport.Pacing();
        var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
        frontend.Initialise();

        return (frontend, tuner, clock);
    }

    private static (CechZd1Frontend Frontend, FakeChip Demodulator, FakeChip Tuner) InitialisedWithBothChips()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
        frontend.Initialise();

        return (frontend, demodulator, tuner);
    }

    private static void Forget(FakeChip chip)
    {
        chip.Written.Clear();
        chip.ReadFrom.Clear();
    }

    private static void LoseADcOffset(FakeChip tuner, int lost) =>
        tuner.Registers[lost] = (ushort)(tuner.Registers[lost] ^ 0x0020);

    private static IReadOnlyList<int> Tables(FakeChip chip, IReadOnlySet<int> tables) =>
        [.. chip.Written.Select(write => write.Register).Where(tables.Contains).Distinct().Order()];

    private static bool Searched(FakeChip demodulator) => demodulator.WritesTo(SearchSequence).Count > 0;

    private static bool Calibrated(FakeChip tuner) =>
        tuner.Written.Any(write => write.Register is SixthBaseband or SeventhBaseband or DcSwitch);

    [Fact]
    public void ATunerIsCalibratedWhenItIsInitialised()
    {
        (_, FakeChip tuner, _) = Initialised();

        Assert.True(Calibrated(tuner));
        Assert.Equal(7, tuner.Last(DcSwitch));
    }

    [Fact]
    public void AWokenTunerKeepsTheDcOffsetsItWasCalibratedWith()
    {
        (CechZd1Frontend frontend, FakeChip tuner, _) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        Forget(tuner);

        SearchOutcome outcome = frontend.Tune(Channel27);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.False(Calibrated(tuner));
    }

    [Fact]
    public void WakingReadsBackTheDcOffsetsBeforeItTrustsThem()
    {
        (CechZd1Frontend frontend, FakeChip tuner, _) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        Forget(tuner);

        frontend.Tune(Channel27);

        Assert.Single(tuner.ReadFrom, register => register is SixthBaseband);
        Assert.Single(tuner.ReadFrom, register => register is SeventhBaseband);
    }

    [Theory]
    [InlineData(SixthBaseband)]
    [InlineData(SeventhBaseband)]
    public void AWokenTunerThatNoLongerHoldsItsDcOffsetsIsCalibratedAgain(int lost)
    {
        (CechZd1Frontend frontend, FakeChip tuner, _) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        LoseADcOffset(tuner, lost);
        Forget(tuner);

        SearchOutcome outcome = frontend.Tune(Channel27);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.True(Calibrated(tuner));
        Assert.Equal(7, tuner.Last(DcSwitch));
    }

    [Theory]
    [InlineData(SixthBaseband)]
    [InlineData(SeventhBaseband)]
    public void AWokenTunerThatNoLongerHoldsItsDcOffsetsHasItsGainRampsWrittenAgain(int lost)
    {
        (CechZd1Frontend frontend, _, FakeChip tuner) = InitialisedWithBothChips();
        frontend.Tune(Channel22);
        frontend.Sleep();
        LoseADcOffset(tuner, lost);
        Forget(tuner);

        frontend.Tune(Channel22);

        Assert.Equal(Dib0090.GainRamps.Order(), Tables(tuner, Dib0090.GainRamps));
    }

    [Theory]
    [InlineData(SixthBaseband)]
    [InlineData(SeventhBaseband)]
    public void AWokenTunerThatNoLongerHoldsItsDcOffsetsHasTheTablesOfTheChannelWrittenAgain(int lost)
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = InitialisedWithBothChips();
        frontend.Tune(Channel22);
        frontend.Sleep();
        LoseADcOffset(tuner, lost);
        Forget(demodulator);

        frontend.Tune(Channel22);

        Assert.Equal(Dib8000.TablesOfAChannel.Order(), Tables(demodulator, Dib8000.TablesOfAChannel));
    }

    [Theory]
    [InlineData(SixthBaseband)]
    [InlineData(SeventhBaseband)]
    public void AWokenTunerThatNoLongerHoldsItsDcOffsetsSearchesForEveryChannelThatLockedBefore(int lost)
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = InitialisedWithBothChips();
        frontend.Tune(Channel22);
        frontend.Tune(Channel27);
        frontend.Sleep();
        LoseADcOffset(tuner, lost);
        Forget(demodulator);

        SearchOutcome outcome = frontend.Tune(Channel22);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.True(Searched(demodulator));
        Forget(demodulator);

        frontend.Tune(Channel27);

        Assert.True(Searched(demodulator));
    }

    [Fact]
    public void AWokenTunerThatHoldsItsDcOffsetsKeepsWhatWasWrittenAndTheChannelsThatLockedBefore()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = InitialisedWithBothChips();
        frontend.Tune(Channel22);
        frontend.Sleep();
        Forget(demodulator);
        Forget(tuner);

        frontend.Tune(Channel22);

        Assert.Empty(Tables(tuner, Dib0090.GainRamps));
        Assert.Empty(Tables(demodulator, Dib8000.TablesOfAChannel));
        Assert.False(Searched(demodulator));
    }

    [Fact]
    public void ATunerCalibratedAgainIsTrustedAgainTheNextTimeItWakes()
    {
        (CechZd1Frontend frontend, FakeChip tuner, _) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        tuner.Registers[SixthBaseband] = (ushort)(tuner.Registers[SixthBaseband] ^ 0x0020);
        frontend.Tune(Channel27);
        frontend.Sleep();
        Forget(tuner);

        frontend.Tune(Channel22);

        Assert.False(Calibrated(tuner));
    }

    [Fact]
    public void AWokenTunerStillMeasuresItsTemperatureAndSetsItsSynthesiser()
    {
        (CechZd1Frontend frontend, FakeChip tuner, _) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        Forget(tuner);

        frontend.Tune(Channel22);

        Assert.NotEmpty(tuner.WritesTo(0x13));
        Assert.Single(tuner.WritesTo(0x15));
        Assert.NotEmpty(tuner.WritesTo(0x18));
    }

    [Fact]
    public void ACalibrationAfterWakingIsStillCountedAsAStageOfItsOwn()
    {
        (CechZd1Frontend frontend, FakeChip tuner, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        tuner.Registers[SeventhBaseband] = (ushort)(tuner.Registers[SeventhBaseband] ^ 0x0800);
        var stages = new StageTimes(clock);

        frontend.Tune(Channel27, stages);

        Assert.Contains("DC offset calibration ", stages.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATuneWithoutACalibrationNamesNoSuchStage()
    {
        (CechZd1Frontend frontend, _, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        frontend.Sleep();
        var stages = new StageTimes(clock);

        frontend.Tune(Channel27, stages);

        Assert.DoesNotContain("DC offset calibration", stages.ToString(), StringComparison.Ordinal);
    }
}
