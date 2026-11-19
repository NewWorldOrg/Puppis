using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295Chips(BridgeI2c i2c)
{
    public const byte Terrestrial = 0x10 << 1;

    public const byte Satellite = 0x11 << 1;

    public const byte Gateway = 0xFE;

    public const byte TunerWrite = 0xC0;

    public const byte TunerRead = 0xC1;

    public const byte TunerReadPointer = 0xFB;

    public void WriteT(byte register, params ReadOnlySpan<byte> values) => i2c.Write(Terrestrial, [register, .. values]);

    public byte[] ReadT(byte register, int count) => Read(Terrestrial, [register], count);

    public void WriteS(byte register, params ReadOnlySpan<byte> values) => i2c.Write(Satellite, [register, .. values]);

    public byte[] ReadS(byte register, int count) => Read(Satellite, [register], count);

    public void WriteRT(byte register, params ReadOnlySpan<byte> values) =>
        i2c.Write(Terrestrial, [Gateway, TunerWrite, register, .. values]);

    public byte ReadRT(byte register)
    {
        i2c.Write(Terrestrial, [Gateway, TunerWrite, TunerReadPointer, register]);

        return Read(Terrestrial, [Gateway, TunerRead], 1)[0];
    }

    public void ModifyRT(byte register, byte keep, byte set) =>
        WriteRT(register, (byte)((ReadRT(register) & keep) | set));

    public void WriteRS(byte register, params ReadOnlySpan<byte> values) =>
        i2c.Write(Satellite, [Gateway, TunerWrite, register, .. values]);

    public byte[] ReadRS(int count) => Read(Satellite, [Gateway, TunerRead], count);

    private byte[] Read(byte address, ReadOnlySpan<byte> written, int count)
    {
        byte[] into = new byte[count];
        i2c.WriteThenRead(address, written, into);

        return into;
    }
}
