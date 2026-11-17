namespace Puppis.Driver.Tests;

/// <summary>
/// The memory an FR30 program runs against: big endian, with whatever registers the test puts behind it.
/// </summary>
public interface IFr30Memory
{
    uint ReadWord(uint address);

    ushort ReadHalf(uint address);

    byte ReadByte(uint address);

    void WriteWord(uint address, uint value);
}

/// <summary>
/// Runs the FR30 instructions the bridge programs here are made of, one at a time, until the program jumps to an
/// address it does not hold. Every other instruction stops the run, so a program that uses one this machine does not
/// know is caught rather than run wrongly.
/// </summary>
public sealed class Fr30Machine(IFr30Memory memory)
{
    public uint[] Registers { get; } = new uint[16];

    public uint ReturnPointer { get; set; }

    public bool Negative { get; private set; }

    public bool Zero { get; private set; }

    public bool Overflow { get; private set; }

    public bool Carry { get; private set; }

    public long Steps { get; private set; }

    /// <summary>
    /// Runs from <paramref name="entry"/> until the program leaves the range it was loaded at, and answers where it went.
    /// </summary>
    public uint Run(uint entry, uint programStart, uint programEnd, long mostSteps = 50_000_000)
    {
        uint pc = entry;

        while (pc >= programStart && pc < programEnd)
        {
            if (++Steps > mostSteps)
            {
                throw new InvalidOperationException($"The program ran more than {mostSteps} instructions.");
            }

            pc = Step(pc);
        }

        return pc;
    }

    private uint Step(uint pc)
    {
        ushort op = memory.ReadHalf(pc);
        int i = op & 0xF;
        int j = (op >> 4) & 0xF;
        uint next = pc + 2;

        switch (op >> 8)
        {
            case 0x00:
                Registers[i] = memory.ReadWord(Registers[13] + Registers[j]);
                return next;
            case 0x01:
                Registers[i] = memory.ReadHalf(Registers[13] + Registers[j]);
                return next;
            case 0x02:
                Registers[i] = memory.ReadByte(Registers[13] + Registers[j]);
                return next;
            case 0x04:
                Registers[i] = memory.ReadWord(Registers[j]);
                return next;
            case 0x05:
                Registers[i] = memory.ReadHalf(Registers[j]);
                return next;
            case 0x06:
                Registers[i] = memory.ReadByte(Registers[j]);
                return next;
            case 0x10:
                memory.WriteWord(Registers[13] + Registers[j], Registers[i]);
                return next;
            case 0x14:
                memory.WriteWord(Registers[j], Registers[i]);
                return next;
            case 0x82:
                Registers[i] = Logic(Registers[i] & Registers[j]);
                return next;
            case 0x92:
                Registers[i] = Logic(Registers[i] | Registers[j]);
                return next;
            case 0x8B:
                Registers[i] = Registers[j];
                return next;
            case 0x97 when j is 0:
                return Registers[i];
            case 0x9B:
                Registers[i] = ((uint)j << 16) | memory.ReadHalf(next);
                return next + 2;
            case 0x9F when op is 0x9FA0:
                return next;
            case 0x9F when j is 8:
                Registers[i] = ((uint)memory.ReadHalf(next) << 16) | memory.ReadHalf(next + 2);
                return next + 4;
            case 0xA4:
                Registers[i] = Add(Registers[i], (uint)j);
                return next;
            case 0xA5:
                Registers[i] = Add(Registers[i], 0xFFFFFFF0u | (uint)j);
                return next;
            case 0xA6:
                Registers[i] = Add(Registers[i], Registers[j]);
                return next;
            case 0xA8:
                Subtract(Registers[i], (uint)j);
                return next;
            case 0xAA:
                Subtract(Registers[i], Registers[j]);
                return next;
            case 0xAC:
                Registers[i] = Subtract(Registers[i], Registers[j]);
                return next;
            case 0xB4:
                Registers[i] = Logic(Registers[i] << j);
                return next;
            case >= 0xC0 and <= 0xCF:
                Registers[i] = (uint)((op >> 4) & 0xFF);
                return next;
            case >= 0xE0 and <= 0xEF:
                return Taken((op >> 8) & 0xF) ? (uint)(next + (2 * (sbyte)(op & 0xFF))) : next;
            default:
                throw new InvalidOperationException($"0x{op:X4} at 0x{pc:X} is not an instruction this machine runs.");
        }
    }

    private bool Taken(int condition) =>
        condition switch
        {
            0x0 => true,
            0x2 => Zero,
            0x3 => !Zero,
            0x4 => Carry,
            0x5 => !Carry,
            0xE => Carry || Zero,
            0xF => !(Carry || Zero),
            _ => throw new InvalidOperationException($"Branch condition 0x{condition:X} is not one this machine runs."),
        };

    private uint Logic(uint result)
    {
        Negative = (result & 0x80000000) is not 0;
        Zero = result is 0;

        return result;
    }

    private uint Add(uint left, uint right)
    {
        ulong sum = (ulong)left + right;
        uint result = (uint)sum;
        Carry = sum > uint.MaxValue;
        Overflow = (~(left ^ right) & (left ^ result) & 0x80000000) is not 0;

        return Logic(result);
    }

    private uint Subtract(uint left, uint right)
    {
        uint result = left - right;
        Carry = left < right;
        Overflow = ((left ^ right) & (left ^ result) & 0x80000000) is not 0;

        return Logic(result);
    }
}
