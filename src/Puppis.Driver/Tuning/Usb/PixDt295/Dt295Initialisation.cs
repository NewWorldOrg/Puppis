namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// One step of setting the chips up: a wait, or a write or a read and write back to one of the four.
/// </summary>
public abstract record ChipStep
{
    public abstract void Apply(Dt295Chips chips, DevicePacing pacing);
}

public sealed record Pause(int Milliseconds) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => pacing.SleepMilliseconds(Milliseconds);
}

public sealed record WriteT(byte Register, byte Value) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.WriteT(Register, Value);
}

public sealed record WriteS(byte Register, byte Value) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.WriteS(Register, Value);
}

public sealed record WriteRT(byte Register, byte Value) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.WriteRT(Register, Value);
}

/// <summary>
/// Sends the terrestrial tuner a register number with no value after it.
/// </summary>
public sealed record PointRT(byte Register) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.WriteRT(Register);
}

public sealed record ModifyRT(byte Register, byte Keep, byte Set) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.ModifyRT(Register, Keep, Set);
}

public sealed record WriteRS(byte Register, byte[] Values) : ChipStep
{
    public override void Apply(Dt295Chips chips, DevicePacing pacing) => chips.WriteRS(Register, Values);
}

/// <summary>
/// The demodulator's two halves and both tuners as they are set up once each time the device is opened. The
/// satellite half is set up and left asleep even for a terrestrial device. The terrestrial demodulator's 0x70 keeps
/// the value it has after the reset.
/// </summary>
public static class Dt295Initialisation
{
    public static readonly IReadOnlyList<ChipStep> Steps =
    [
        new Pause(20),
        new Pause(5),
        new WriteT(0x01, 0x00),
        new WriteT(0x03, 0x90),
        new WriteT(0x06, 0x80),
        new WriteT(0x1C, 0x20),
        new WriteT(0x1D, 0x56),
        new WriteT(0x1E, 0x6A),
        new WriteT(0x1F, 0x9A),
        new WriteT(0x23, 0x4D),
        new WriteT(0x25, 0x00),
        new WriteT(0x26, 0x00),
        new WriteT(0x32, 0x21),
        new WriteT(0x3B, 0x21),
        new WriteT(0x3C, 0x3F),
        new WriteT(0x73, 0x45),
        new WriteT(0x74, 0x11),
        new WriteT(0x75, 0x02),
        new WriteT(0x76, 0x00),
        new WriteT(0x77, 0x01),
        new WriteT(0x7D, 0xD2),
        new WriteT(0xE4, 0xF4),
        new WriteT(0xF4, 0x80),
        new PointRT(0xFF),
        new WriteRT(0x00, 0x01),
        new WriteRT(0x96, 0x00),
        new WriteRT(0x00, 0x00),
        new WriteRT(0x14, 0x13),
        new WriteRT(0x6D, 0x8A),
        new WriteRT(0x6D, 0x0A),
        new WriteRT(0xDF, 0x19),
        new WriteRT(0x45, 0x1B),
        new WriteRT(0xA9, 0x59),
        new WriteRT(0xAA, 0x6A),
        new WriteRT(0xBE, 0x4C),
        new WriteRT(0xCF, 0x2A),
        new WriteRT(0xD2, 0xF5),
        new WriteRT(0xD0, 0x34),
        new WriteRT(0x77, 0xE7),
        new WriteRT(0x78, 0xE3),
        new WriteRT(0x6F, 0x51),
        new WriteRT(0x7B, 0x84),
        new WriteRT(0x7C, 0x9F),
        new WriteRT(0x56, 0x41),
        new WriteRT(0xCD, 0x64),
        new WriteRT(0xC3, 0x2C),
        new WriteRT(0x9D, 0x61),
        new WriteRT(0xF7, 0x52),
        new WriteRT(0x58, 0x81),
        new WriteRT(0xB1, 0x30),
        new WriteRT(0x00, 0x01),
        new ModifyRT(0x31, 0x2F, 0xD0),
        new WriteRT(0x00, 0x00),
        new WriteRT(0x01, 0x89),
        new WriteRT(0x02, 0x00),
        new WriteRT(0x6D, 0x0A),
        new WriteRT(0x0B, 0x01),
        new ModifyRT(0x04, 0xC0, 0x01),
        new WriteRT(0x05, 0x1B),
        new ModifyRT(0x08, 0xF2, 0x05),
        new ModifyRT(0x09, 0x80, 0x42),
        new ModifyRT(0x5E, 0xEF, 0x00),
        new WriteRT(0x0A, 0x00),
        new WriteRT(0x0C, 0x00),
        new WriteRT(0x13, 0x04),
        new WriteRT(0x53, 0xFE),
        new WriteRT(0x57, 0x91),
        new WriteRT(0x5A, 0xF9),
        new WriteRT(0x5B, 0x18),
        new WriteRT(0x5C, 0xF1),
        new WriteRT(0x62, 0xC2),
        new WriteRT(0x6E, 0x01),
        new WriteRT(0x6F, 0x51),
        new WriteRT(0x87, 0x77),
        new WriteRT(0x88, 0x55),
        new WriteRT(0x93, 0x22),
        new WriteRT(0x97, 0x02),
        new WriteRT(0xBA, 0x30),
        new WriteRT(0x98, 0xAF),
        new WriteRT(0x9B, 0x20),
        new WriteRT(0x9C, 0x1E),
        new WriteRT(0xA0, 0x18),
        new WriteRT(0xA5, 0x09),
        new WriteRT(0xC2, 0xA9),
        new WriteRT(0xC5, 0x7C),
        new WriteRT(0xCD, 0xE8),
        new WriteRT(0xCE, 0x7F),
        new WriteRT(0xD5, 0x03),
        new WriteRT(0xD9, 0x04),
        new WriteRT(0xEA, 0x0D),
        new WriteRT(0xDC, 0x1C),
        new WriteRT(0x6D, 0x0A),
        new WriteRT(0x03, 0x00),
        new WriteRT(0x03, 0x01),
        new Pause(100),
        new ModifyRT(0x0D, 0xFE, 0x00),
        new WriteRT(0x0F, 0x20),
        new WriteRT(0x10, 0x49),
        new WriteRT(0x11, 0x76),
        new WriteRT(0x00, 0x01),
        new WriteRT(0x31, 0x81),
        new WriteRT(0x00, 0x00),
        new WriteRT(0x7C, 0x1F),
        new WriteRT(0x0B, 0x01),
        new WriteRT(0x12, 0x01),
        new Pause(20),
        new WriteRT(0x12, 0x00),
        new WriteRT(0x0B, 0x00),
        new WriteT(0x25, 0x00),
        new WriteT(0x23, 0x4D),
        new WriteT(0x03, 0x90),
        new WriteS(0x01, 0x90),
        new WriteS(0x03, 0x00),
        new WriteS(0x04, 0x02),
        new WriteS(0x07, 0x33),
        new WriteS(0x08, 0x11),
        new WriteS(0x10, 0xB0),
        new WriteS(0x11, 0x00),
        new WriteS(0x13, 0x80),
        new WriteS(0x15, 0x00),
        new WriteS(0x17, 0x01),
        new WriteS(0x1C, 0x59),
        new WriteS(0x1D, 0xAA),
        new WriteS(0x1E, 0x20),
        new WriteS(0x1F, 0x19),
        new WriteS(0x85, 0x7A),
        new WriteS(0x87, 0x04),
        new WriteS(0x8E, 0x26),
        new WriteS(0xA3, 0x91),
        new WriteS(0xA5, 0x40),
        new Pause(4),
        new WriteRS(0x2A, [0xB9]),
        new WriteRS(0x06, [0x10, 0x10, 0x00]),
        new WriteRS(0x0A, [0x00]),
        new WriteRS(0x0E, [0x00]),
        new WriteRS(0x2A, [0xB9]),
        new WriteRS(0x29, [0x70]),
        new WriteRS(0x28, [0x3E]),
        new WriteRS(0x2A, [0x19]),
        new WriteRS(0x1C, [0x00]),
        new WriteRS(0x11, [0xC0, 0xA7]),
        new Pause(6),
    ];

    public static void Run(Dt295Chips chips, DevicePacing pacing)
    {
        foreach (ChipStep step in Steps)
        {
            step.Apply(chips, pacing);
        }
    }
}
