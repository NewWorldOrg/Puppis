using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The bridge's smart-card UART, which the card slot hangs on: powering the card, and moving bytes one register access
/// at a time.
/// </summary>
public sealed class CardUart(BridgeMemory memory, DevicePacing pacing)
{
    public const uint Enable = 0xFEA000;
    public const uint Line = 0xFEA004;
    public const uint Speed = 0xFEA008;
    public const uint Transmit = 0xFEA00C;
    public const uint Control = 0xFEA010;
    public const uint Received = 0xFEA014;
    public const uint InterruptMask = 0xFEA018;
    public const uint Status = 0xFEA01C;
    public const uint CharacterWait = 0xFEA020;
    public const uint BlockWait = 0xFEA024;
    public const uint SendByte = 0xFEA028;
    public const uint ReceiveByte = 0xFEA02C;
    public const uint Power = 0xFEA034;
    public const uint SendCheck = 0xFEA038;
    public const uint ReceiveCheck = 0xFEA03C;

    public const uint CardInserted = 0x400;

    public const int LongFrame = 256;

    public static readonly TimeSpan AnswerToResetPatience = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan ReplyPatience = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(50);

    public static readonly IReadOnlyList<(uint Address, uint Value, int Milliseconds)> PowerOn =
    [
        (InterruptMask, 0, 0),
        (Power, 0, 0),
        (Status, 0x3FF, 0),
        (Line, 0, 0),
        (Received, 0, 0),
        (Enable, 0, 20),
        (Enable, 7, 20),
        (Line, 8, 0),
        (Speed, 0xBA7F, 20),
        (Line, 0x0A, 0),
        (Enable, 0, 20),
        (Enable, 7, 0),
        (ReceiveCheck, 0x3B, 0),
        (CharacterWait, 9600, 0),
        (BlockWait, 0, 0),
        (InterruptMask, 0, 0),
        (Received, 0x20, 50),
        (Power, 1, 0),
        (Line, 0x0E, 0),
    ];

    public static readonly IReadOnlyList<(uint Address, uint Value)> FasterSpeed =
    [
        (Speed, 0x5D5D),
        (Control, 0x00010000),
        (CharacterWait, 107),
        (BlockWait, 36559),
    ];

    public static readonly IReadOnlyList<(uint Address, uint Value, int Milliseconds)> PowerOff =
    [
        (Received, 0, 0),
        (Enable, 3, 0),
        (Line, 0, 0),
        (InterruptMask, 0, 0),
        (Power, 0, 1),
        (InterruptMask, 0, 0),
        (Power, 0, 0),
        (Line, 4, 0),
        (Received, 0x20, 0),
        (Enable, 7, 0),
        (Speed, 0xBA7F, 1),
        (ReceiveCheck, 0, 0),
        (CharacterWait, 0, 0),
        (BlockWait, 0, 0),
        (Transmit, 0, 0),
        (Control, 0x200, 0),
        (SendCheck, 0, 0),
    ];

    public string Path => memory.Path;

    public bool CardIsIn() => (memory.Read(Status) & CardInserted) is not 0;

    public void SwitchOn() => Run(PowerOn);

    public void SwitchOff() => Run(PowerOff);

    public void SpeakFaster()
    {
        foreach ((uint at, uint value) in FasterSpeed)
        {
            memory.Write(at, value);
        }
    }

    public void Send(ReadOnlySpan<byte> frame)
    {
        memory.Write(ReceiveCheck, 0);
        memory.Write(SendCheck, 0);

        foreach (byte value in frame)
        {
            memory.Write(SendByte, value);
        }

        memory.Write(Status, 0x3FF);
        memory.Write(Control, frame.Length >= LongFrame ? 0x280u : 0x200u);
        memory.Write(Transmit, 1);
    }

    /// <summary>
    /// Collects what the card sends within <paramref name="patience"/>, until <paramref name="enough"/> says the
    /// bytes so far are whole or the card has been silent for a while after it started.
    /// </summary>
    public byte[] Receive(TimeSpan patience, Func<IReadOnlyList<byte>, bool> enough)
    {
        long started = pacing.Timestamp;
        long? lastByte = null;
        List<byte> bytes = [];

        while (true)
        {
            int waiting = (int)(memory.Read(Received) >> 16);

            for (int index = 0; index < waiting; index++)
            {
                bytes.Add((byte)memory.Read(ReceiveByte));
            }

            if (waiting > 0)
            {
                lastByte = pacing.Timestamp;

                if (enough(bytes))
                {
                    return [.. bytes];
                }

                continue;
            }

            if ((lastByte is { } last && pacing.Since(last) >= Silence) || pacing.Since(started) >= patience)
            {
                return [.. bytes];
            }

            pacing.SleepMilliseconds(1);
        }
    }

    private void Run(IReadOnlyList<(uint Address, uint Value, int Milliseconds)> steps)
    {
        foreach ((uint at, uint value, int milliseconds) in steps)
        {
            memory.Write(at, value);

            if (milliseconds > 0)
            {
                pacing.SleepMilliseconds(milliseconds);
            }
        }
    }

    public static SmartCardException NoCard(string path) =>
        new($"{path}: no card is in the tuner's slot.", SmartCardCodes.NoSmartCard);
}
