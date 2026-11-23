using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class UnchangedWriteSkippingBusTests
{
    private static readonly IReadOnlySet<int> Listed = new HashSet<int> { 80, 81 };

    [Fact]
    public void AListedRegisterIsNotWrittenAgainWithTheValueItHolds()
    {
        var chip = new FakeChip(2);
        var bus = new UnchangedWriteSkippingBus(chip, Listed);

        bus.Write(80, 7250);
        bus.Write(80, 7250);
        bus.Write(81, 7250);

        Assert.Equal([(80, (ushort)7250), (81, (ushort)7250)], chip.Written);
    }

    [Fact]
    public void AListedRegisterIsWrittenWhenItsValueChangesAndWhenItChangesBack()
    {
        var chip = new FakeChip(2);
        var bus = new UnchangedWriteSkippingBus(chip, Listed);

        bus.Write(80, 7250);
        bus.Write(80, 6895);
        bus.Write(80, 7250);

        Assert.Equal([(ushort)7250, (ushort)6895, (ushort)7250], chip.WritesTo(80));
    }

    [Fact]
    public void ARegisterThatIsNotListedIsWrittenEveryTime()
    {
        var chip = new FakeChip(2);
        var bus = new UnchangedWriteSkippingBus(chip, Listed);

        bus.Write(770, 0x4000);
        bus.Write(770, 0);
        bus.Write(770, 0);
        bus.Write(0, 0x6000);
        bus.Write(0, 0x6000);

        Assert.Equal(5, chip.Written.Count);
    }

    [Fact]
    public void EveryReadGoesToTheChip()
    {
        var chip = new FakeChip(2);
        var bus = new UnchangedWriteSkippingBus(chip, Listed);
        bus.Write(80, 7250);
        chip.Registers[80] = 1;

        Assert.Equal(1, bus.Read(80));
        Assert.Equal(1, bus.Read(80));
        Assert.Equal([80, 80], chip.ReadFrom);
    }

    [Fact]
    public void OnceForgottenEveryRegisterIsWrittenAgain()
    {
        var chip = new FakeChip(2);
        var bus = new UnchangedWriteSkippingBus(chip, Listed);
        bus.Write(80, 7250);

        bus.Forget();
        bus.Write(80, 7250);

        Assert.Equal(2, chip.WritesTo(80).Count);
    }

    [Fact]
    public void AWriteThatFailedIsTriedAgainWithTheSameValue()
    {
        var failing = new FailingOnce();
        var bus = new UnchangedWriteSkippingBus(failing, Listed);
        bus.Write(80, 7250);
        failing.FailNext = true;

        Assert.Throws<DvbDeviceException>(() => bus.Write(80, 6895));

        bus.Write(80, 7250);
        bus.Write(80, 6895);

        Assert.Equal([(ushort)7250, (ushort)7250, (ushort)6895], failing.Reached);
    }

    private sealed class FailingOnce : IRegisterBus
    {
        public bool FailNext { get; set; }

        public List<ushort> Reached { get; } = [];

        public ushort Read(int register) => 0;

        public void Write(int register, ushort value)
        {
            if (FailNext)
            {
                FailNext = false;

                throw DvbFailure.Refused("the chip did not acknowledge");
            }

            Reached.Add(value);
        }
    }
}
