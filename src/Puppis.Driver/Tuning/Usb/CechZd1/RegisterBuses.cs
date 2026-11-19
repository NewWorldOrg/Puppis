using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.CechZd1;

public interface IRegisterBus
{
    ushort Read(int register);

    void Write(int register, ushort value);
}

public static class RegisterBusExtensions
{
    public static void Modify(this IRegisterBus bus, int register, ushort clear, ushort set) =>
        bus.Write(register, (ushort)((bus.Read(register) & ~clear) | set));

    public static uint ReadLong(this IRegisterBus bus, int upper) =>
        ((uint)bus.Read(upper) << 16) | bus.Read(upper + 1);

    public static void WriteLong(this IRegisterBus bus, int upper, uint value)
    {
        bus.Write(upper, (ushort)(value >> 16));
        bus.Write(upper + 1, (ushort)value);
    }
}

public sealed class Dib8000Bus(BridgeI2c i2c) : IRegisterBus
{
    public const byte Address = 0x12;

    public ushort Read(int register)
    {
        Span<byte> value = stackalloc byte[2];
        i2c.WriteThenRead(Address, [(byte)(register >> 8), (byte)register], value);

        return (ushort)((value[0] << 8) | value[1]);
    }

    public void Write(int register, ushort value) =>
        i2c.Write(Address, [(byte)(register >> 8), (byte)register, (byte)(value >> 8), (byte)value]);
}

public sealed class Dib0090Bus(BridgeI2c i2c) : IRegisterBus
{
    public const byte Address = 0xC0;

    public ushort Read(int register)
    {
        Span<byte> value = stackalloc byte[2];
        i2c.WriteThenRead(Address, [(byte)register], value);

        return (ushort)((value[0] << 8) | value[1]);
    }

    public void Write(int register, ushort value) =>
        i2c.Write(Address, [(byte)register, (byte)(value >> 8), (byte)value]);
}
