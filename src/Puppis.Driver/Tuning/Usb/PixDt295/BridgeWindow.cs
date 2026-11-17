using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// Reaches the bridge's addresses from 0x1000000 up by moving the window its memory requests see at 0x80000, and
/// puts the window back after every access, so that what lies behind it at power on stays in reach.
/// </summary>
public sealed class BridgeWindow(BridgeMemory memory)
{
    public const uint ReadWindow = 0xE9EBC;

    public const uint WriteWindow = 0xEA3D8;

    public const uint AtPowerOn = 0x00F00000;

    public const uint Opening = 0x80000;

    public const uint Span = 0x20000;

    private const uint Offset = Span - 1;

    private const uint Region = ~Offset;

    private readonly Lock gate = new();
    private bool moved;

    public BridgeMemory Memory => memory;

    public uint Read(uint address) => ReadWords(address, 1)[0];

    public uint[] ReadWords(uint address, int count)
    {
        WithinOneRegion(address, count);

        lock (gate)
        {
            Open(ReadWindow, address);

            try
            {
                return memory.ReadWords(Opening + (address & Offset), count);
            }
            finally
            {
                Close(ReadWindow);
            }
        }
    }

    public void Write(uint address, uint word)
    {
        lock (gate)
        {
            Open(WriteWindow, address);

            try
            {
                memory.Write(Opening + (address & Offset), word);
            }
            finally
            {
                Close(WriteWindow);
            }
        }
    }

    /// <summary>
    /// Writes the word and reads it back, refusing when the bridge holds something else.
    /// </summary>
    public void WriteAndCheck(uint address, uint word)
    {
        Write(address, word);
        uint found = Read(address);

        if (found != word)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: 0x{address:X8} read back as 0x{found:X8} after 0x{word:X8} was written to it."
            );
        }
    }

    public static DvbDeviceException Unplug(string path, string what) =>
        new(
            $"{path}: {what}, which is neither how the bridge powers on nor what this driver leaves behind. Unplug the tuner and plug it back in.",
            TuningFailure.DeviceUnusable
        );

    private void Open(uint window, uint address)
    {
        uint found = memory.Read(window);

        if (found != AtPowerOn)
        {
            throw Unplug(memory.Path, $"the window word at 0x{window:X} holds 0x{found:X8}");
        }

        moved = true;

        try
        {
            Place(window, address & Region);
        }
        catch (DvbDeviceException)
        {
            PutBackQuietly(window);

            throw;
        }
    }

    private bool PutBackQuietly(uint window)
    {
        try
        {
            memory.Write(window, AtPowerOn);

            return true;
        }
        catch (DvbDeviceException)
        {
            return false;
        }
    }

    private void Close(uint window) => Place(window, AtPowerOn);

    /// <summary>
    /// Writes both window words back to how they are at power on, when this window has ever moved one, whatever
    /// became of the moves; the last thing a closing tuner does to the bridge.
    /// </summary>
    public void PutBack()
    {
        lock (gate)
        {
            if (!moved)
            {
                return;
            }

            memory.Write(ReadWindow, AtPowerOn);
            memory.Write(WriteWindow, AtPowerOn);
        }
    }

    private void Place(uint window, uint value)
    {
        memory.Write(window, value);
        uint found = memory.Read(window);

        if (found != value)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: the window word at 0x{window:X} read back as 0x{found:X8} after 0x{value:X8} was written."
            );
        }
    }

    private static void WithinOneRegion(uint address, int count)
    {
        uint last = address + (uint)((4 * count) - 1);

        if ((last & Region) != (address & Region))
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"A read through the window stays inside one 128 KiB region, and {count} words from 0x{address:X8} cross out of it."
            );
        }
    }
}
