using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.Bridge;

public sealed class BridgeI2c(BridgeMemory memory)
{
    public const uint Status = 0xFD8FE0;
    public const uint Control = 0xFD8FE4;
    public const uint Clock = 0xFD8FE8;
    public const uint Data = 0xFD8FF0;
    public const uint Divider = 0xFD8FF4;

    public const uint SendAfterStart = 0x50;
    public const uint SendAfterRepeatedStart = 0x70;
    public const uint ReceiveAndAcknowledge = 0x58;
    public const uint Idle = 0x40;

    public const uint StatusBusy = 0x80;
    public const uint StatusNoAcknowledge = 0x10;
    public const uint ControlBusError = 0x80;
    public const uint ControlByteDone = 0x01;

    public const int MostPolls = 2000;

    public void Write(byte address, ReadOnlySpan<byte> bytes)
    {
        using HeldTurn turn = memory.TakeTurn();

        try
        {
            Begin(address, bytes);
            End();
        }
        catch
        {
            Release();

            throw;
        }
    }

    public void WriteThenRead(byte address, ReadOnlySpan<byte> bytes, Span<byte> into)
    {
        using HeldTurn turn = memory.TakeTurn();

        try
        {
            Begin(address, bytes);
            Send((uint)(address | 1), SendAfterRepeatedStart, address, "its address for reading");

            for (int index = 0; index < into.Length; index++)
            {
                bool last = index == into.Length - 1;
                memory.Write(Control, last ? SendAfterStart : ReceiveAndAcknowledge);
                WaitForTheByte(address);
                into[index] = (byte)memory.Read(Data);
            }

            End();
        }
        catch
        {
            Release();

            throw;
        }
    }

    private void Begin(byte address, ReadOnlySpan<byte> bytes)
    {
        WaitUntilIdle(address);
        Send(address, SendAfterStart, address, "its address");

        foreach (byte value in bytes)
        {
            Send(value, SendAfterStart, address, "a data byte");
        }
    }

    private void End() => memory.Write(Control, Idle);

    private void Release() => memory.Write(Control, Idle);

    private void Send(uint value, uint control, byte address, string what)
    {
        memory.Write(Data, value);
        memory.Write(Control, control);
        uint status = WaitForTheByte(address);

        if ((status & StatusNoAcknowledge) is not 0)
        {
            throw Fault($"the device at 0x{address:X2} did not acknowledge {what}");
        }
    }

    private uint WaitForTheByte(byte address)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            uint[] words = memory.ReadWords(Status, 2);
            uint control = words[1];

            if ((control & ControlBusError) is not 0)
            {
                throw Fault($"the bus reported an error while talking to 0x{address:X2}");
            }

            if ((control & ControlByteDone) is not 0)
            {
                return words[0];
            }
        }

        throw Fault($"a byte to 0x{address:X2} did not finish within {MostPolls} polls");
    }

    private void WaitUntilIdle(byte address)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            if ((memory.Read(Status) & StatusBusy) is 0)
            {
                return;
            }
        }

        throw Fault($"the bus stayed busy for {MostPolls} polls around 0x{address:X2}");
    }

    private DvbDeviceException Fault(string what) =>
        new($"{memory.Path}: I2C: {what}.", TuningFailure.DeviceUnusable);
}
