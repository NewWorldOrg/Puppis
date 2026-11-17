using System.Buffers.Binary;
using System.Security.Cryptography;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// Brings the bridge from how it powers on to a running I2C master with the demodulator out of reset.
/// </summary>
public static class Dt295Startup
{
    public const uint SetUpMark = 0x82008;

    public const uint HandlerStart = 0xE9000;

    public const int HandlerBytes = 0x3910;

    public const string HandlerAtPowerOn = "0bdf3af8c2d4ab49edd69740bd751e698e337e2df542ac4cedcbe4ba99c3e9b0";

    public const uint GpioDirection = 0xF10014;

    public const uint GpioValue = 0xF10018;

    public const ushort DemodulatorReset = 0x0002;

    public static void RequirePowerOnState(BridgeMemory memory)
    {
        ushort mark = memory.ReadSixteen(SetUpMark);

        if (mark is not 0)
        {
            throw BridgeWindow.Unplug(
                memory.Path,
                $"something other than this driver has set the bridge up since it was powered on (0x{SetUpMark:X} reads 0x{mark:X4})"
            );
        }
    }

    /// <summary>
    /// Refuses a bridge whose request handling is not the code the hook was written for. The two words the hook
    /// replaces are counted as they are at power on, whether or not the hook is in place.
    /// </summary>
    public static void RequireTheKnownHandler(BridgeMemory memory, string expected = HandlerAtPowerOn)
    {
        uint[] words = memory.ReadWords(HandlerStart, HandlerBytes / 4);
        int hook = (int)(CopyProgram.HookAt - HandlerStart) / 4;

        if (CopyProgram.IsOurHook(words[hook], words[hook + 1]))
        {
            words[hook] = CopyProgram.HookAtPowerOn[0];
            words[hook + 1] = CopyProgram.HookAtPowerOn[1];
        }
        else if (words[hook] != CopyProgram.HookAtPowerOn[0] || words[hook + 1] != CopyProgram.HookAtPowerOn[1])
        {
            throw BridgeWindow.Unplug(
                memory.Path,
                $"the bridge's write handling at 0x{CopyProgram.HookAt:X} holds 0x{words[hook]:X8} 0x{words[hook + 1]:X8}"
            );
        }

        byte[] bytes = new byte[HandlerBytes];

        for (int word = 0; word < words.Length; word++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 * word), words[word]);
        }

        string found = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (found != expected)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: the bridge's request handling at 0x{HandlerStart:X} is not the code this driver hooks into, so the tuner is not opened."
            );
        }
    }

    public static void ResetTheDemodulator(BridgeMemory memory, DevicePacing pacing)
    {
        memory.WriteSixteen(GpioDirection, DemodulatorReset);
        memory.WriteSixteen(GpioValue, 0x0000);
        pacing.SleepMilliseconds(20);
        memory.WriteSixteen(GpioValue, DemodulatorReset);
        pacing.SleepMilliseconds(5);
    }

    public static void StartClocks(BridgeMemory memory)
    {
        memory.WriteSixteen(0xF10118, 0xFFFF);
        memory.WriteSixteen(0xF1011C, (ushort)((memory.ReadSixteen(0xF1011C) & 0xFA1F) | 0x0580));
        memory.Write(0xF001F0, 0x1A000000);
    }

    public static void StartI2cMaster(BridgeMemory memory)
    {
        memory.WriteSixteen(0xF10114, (ushort)(memory.ReadSixteen(0xF10114) | 0x0020));
        memory.Modify(0xFD8010, 0, 0x10);
        memory.Modify(0xFD8014, 0x10, 0);
        memory.Write(0xFD80C0, 1);
        memory.Write(0xFD80C0, 0);
        memory.Write(0xFD80C4, 0);
        memory.Write(0xFD8FF8, 0x06);
        memory.Write(BridgeI2c.Divider, 0x10);
        memory.Write(BridgeI2c.Clock, 0x3C);
        memory.Write(BridgeI2c.Control, BridgeI2c.Idle);
    }
}
