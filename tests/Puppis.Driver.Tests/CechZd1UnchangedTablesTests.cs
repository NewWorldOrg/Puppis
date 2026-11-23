using Puppis.Contracts;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class CechZd1UnchangedTablesTests
{
    private const string Device = "/dev/bus/usb/001/002";

    private static readonly long Channel22 = BroadcastStandards.TerrestrialCentreHz(22);

    private static readonly long Channel27 = BroadcastStandards.TerrestrialCentreHz(27);

    private static readonly int[] TablesOfTheDemodulator =
    [
        80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
        116,
        178,
        180, 181, 182, 183, 184, 185, 186, 187,
        189, 192,
        205,
        211,
        215, 216, 217, 218,
        222, 223,
        225,
        228,
        250,
        265, 266,
        287, 288,
        290, 291, 292,
        338, 340, 341,
        351, 352, 353,
    ];

    private static readonly int[] RampsOfTheTuner =
    [
        0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31,
        0x35, 0x36, 0x37, 0x38,
        0x3E, 0x3F,
    ];

    private static readonly int[] StartSomethingWhenWritten = [0, 1, 10, 770, 771, 1286, 1291];

    private static (CechZd1Frontend Frontend, FakeChip Demodulator, FakeChip Tuner) Initialised()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
        frontend.Initialise();

        return (frontend, demodulator, tuner);
    }

    private static void Forget(params FakeChip[] chips)
    {
        foreach (FakeChip chip in chips)
        {
            chip.Written.Clear();
        }
    }

    private static IReadOnlyList<int> Tables(FakeChip chip, IReadOnlySet<int> tables) =>
        [.. chip.Written.Select(write => write.Register).Where(tables.Contains)];

    private static IReadOnlyList<(int Register, ushort Value)> Others(FakeChip chip, IReadOnlySet<int> tables) =>
        [.. chip.Written.Where(write => !tables.Contains(write.Register))];

    [Fact]
    public void TheDemodulatorRegistersLeftAloneAreTheTablesOfTheChannelConfigurationAndNoOthers()
    {
        Assert.Equal(TablesOfTheDemodulator, Dib8000.TablesOfAChannel.Order());
    }

    [Fact]
    public void TheTunerRegistersLeftAloneAreItsGainRampsAndNoOthers()
    {
        Assert.Equal(RampsOfTheTuner, Dib0090.GainRamps.Order());
    }

    [Fact]
    public void NoRegisterThatStartsSomethingWhenWrittenIsAmongThem()
    {
        Assert.DoesNotContain(StartSomethingWhenWritten, Dib8000.TablesOfAChannel.Contains);
        Assert.DoesNotContain(0x04, Dib0090.GainRamps);
        Assert.DoesNotContain(0x2A, Dib0090.GainRamps);
        Assert.DoesNotContain(0x33, Dib0090.GainRamps);
        Assert.DoesNotContain(0x32, Dib0090.GainRamps);
        Assert.DoesNotContain(0x39, Dib0090.GainRamps);
    }

    [Fact]
    public void ATuneOfTheChannelTheTablesWereWrittenForWritesNoneOfThemAgain()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator, tuner);

        SearchOutcome outcome = frontend.Tune(Channel22);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.Empty(Tables(demodulator, Dib8000.TablesOfAChannel));
        Assert.Empty(Tables(tuner, Dib0090.GainRamps));
    }

    [Fact]
    public void TheTablesStayAsWrittenThroughSleepAndAChangeOfFrequency()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = Initialised();
        frontend.Tune(Channel22);
        frontend.Tune(Channel27);
        frontend.Sleep();
        Forget(demodulator, tuner);

        frontend.Tune(Channel22);

        Assert.Empty(Tables(demodulator, Dib8000.TablesOfAChannel));
        Assert.Empty(Tables(tuner, Dib0090.GainRamps));
    }

    [Fact]
    public void EveryOtherRegisterIsWrittenExactlyAsItWasWhenTheTablesWereWrittenToo()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator, tuner);
        frontend.Tune(Channel22);
        IReadOnlyList<(int Register, ushort Value)> demodulatorBefore = Others(demodulator, Dib8000.TablesOfAChannel);
        IReadOnlyList<(int Register, ushort Value)> tunerBefore = Others(tuner, Dib0090.GainRamps);
        Forget(demodulator, tuner);

        frontend.Tune(Channel22);

        Assert.Equal(demodulatorBefore, demodulator.Written);
        Assert.Equal(tunerBefore, tuner.Written);
        Assert.Contains(demodulator.Written, write => write.Register is 0);
        Assert.Contains(demodulator.Written, write => write.Register is 770);
        Assert.Equal(48, demodulator.Written.Count(write => write.Register is >= 117 and <= 140));
        Assert.Contains(tuner.Written, write => write.Register is 0x04);
        Assert.Contains(tuner.Written, write => write.Register is 0x2A);
    }

    [Fact]
    public void ATableIsWrittenWhenTheChannelNeedsAnotherValueAndAgainWhenTheFirstComesBack()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        demodulator.Answer(487, 1);
        demodulator.Answer(488, 1);
        Forget(demodulator);

        frontend.Tune(Channel27);

        Assert.Equal(7250 - 355, demodulator.Last(80));
        Assert.Equal(0x099A, demodulator.Last(215));
        Forget(demodulator);

        frontend.Tune(Channel22);

        Assert.Equal(7250, demodulator.Last(80));
        Assert.Equal(0x0148, demodulator.Last(215));
    }

    [Fact]
    public void InitialisingAgainWritesEveryDefaultWhateverWasWrittenBefore()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, FakeChip tuner) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator, tuner);

        frontend.Initialise();

        Assert.Equal([(ushort)7250], demodulator.WritesTo(80));
        Assert.Equal([(ushort)0x2800], demodulator.WritesTo(181));
        Forget(demodulator, tuner);

        frontend.Tune(Channel22);

        Assert.NotEmpty(Tables(tuner, Dib0090.GainRamps));
        Assert.NotEmpty(Tables(demodulator, Dib8000.TablesOfAChannel));
    }
}
