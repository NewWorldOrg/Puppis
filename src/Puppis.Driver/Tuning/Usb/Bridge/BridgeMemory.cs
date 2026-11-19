using System.Buffers.Binary;

using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.Bridge;

/// <summary>
/// The bridge's memory, registers included, through its vendor request. Words travel big endian.
/// </summary>
public sealed class BridgeMemory(TurnTakingConnection usb)
{
    public const byte MemoryRequest = 0xBC;

    public const byte ReadRequestType = 0xC0;

    public const byte WriteRequestType = 0x40;

    public const int MostWordsPerRequest = 16;

    public const uint FirstAddressOutOfReachAtPowerOn = 0x1000000;

    private const int WordBytes = 4;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    private long requests;

    /// <summary>
    /// Whether the bridge takes full 32 bit addresses and writes of more than one word.
    /// </summary>
    public bool Widened { get; private set; }

    public string Path => usb.Path;

    public long Requests => Interlocked.Read(ref requests);

    public static UsbSetup SetupFor(byte requestType, uint address) =>
        new(
            requestType,
            MemoryRequest,
            (ushort)((((address >> 16) & 0xFF) << 8) | ((address >> 24) & 0xFF)),
            (ushort)address
        );

    public void MarkWidened() => Widened = true;

    public HeldTurn TakeTurn() => usb.TakeTurn();

    public uint Read(uint address) => ReadWords(address, 1)[0];

    public uint[] ReadWords(uint address, int count)
    {
        uint[] words = new uint[count];
        byte[] bytes = new byte[MostWordsPerRequest * WordBytes];

        for (int done = 0; done < count; done += MostWordsPerRequest)
        {
            int taking = Math.Min(MostWordsPerRequest, count - done);
            uint at = address + (uint)(done * WordBytes);
            Reachable(at, taking);
            Span<byte> asked = bytes.AsSpan(0, taking * WordBytes);
            Interlocked.Increment(ref requests);
            int arrived = usb.ControlIn(SetupFor(ReadRequestType, at), asked, Patience);

            if (arrived != asked.Length)
            {
                throw DvbFailure.Refused(
                    $"{usb.Path}: the bridge answered {arrived} of the {asked.Length} bytes asked for at 0x{at:X}."
                );
            }

            for (int word = 0; word < taking; word++)
            {
                words[done + word] = BinaryPrimitives.ReadUInt32BigEndian(asked[(word * WordBytes)..]);
            }
        }

        return words;
    }

    public void Write(uint address, uint word) => WriteWords(address, [word]);

    public void WriteWords(uint address, ReadOnlySpan<uint> words)
    {
        int chunk = Widened ? MostWordsPerRequest : 1;
        byte[] bytes = new byte[chunk * WordBytes];

        for (int done = 0; done < words.Length; done += chunk)
        {
            int taking = Math.Min(chunk, words.Length - done);
            uint at = address + (uint)(done * WordBytes);
            Reachable(at, taking);

            for (int word = 0; word < taking; word++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(word * WordBytes), words[done + word]);
            }

            Interlocked.Increment(ref requests);
            usb.ControlOut(SetupFor(WriteRequestType, at), bytes.AsSpan(0, taking * WordBytes), Patience);
        }
    }

    /// <summary>
    /// Writes every word in one request, which a bridge whose addresses were never widened takes for as many as two.
    /// </summary>
    public void WriteInOneRequest(uint address, ReadOnlySpan<uint> words)
    {
        Reachable(address, words.Length);
        byte[] bytes = new byte[words.Length * WordBytes];

        for (int word = 0; word < words.Length; word++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(word * WordBytes), words[word]);
        }

        Interlocked.Increment(ref requests);
        usb.ControlOut(SetupFor(WriteRequestType, address), bytes, Patience);
    }

    /// <summary>
    /// Reads a 16 bit register with a request for its two bytes alone.
    /// </summary>
    public ushort ReadSixteen(uint address)
    {
        Reachable(address, 1);
        Span<byte> bytes = stackalloc byte[2];
        Interlocked.Increment(ref requests);
        int arrived = usb.ControlIn(SetupFor(ReadRequestType, address), bytes, Patience);

        return arrived is 2
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
            : throw DvbFailure.Refused(
                $"{usb.Path}: the bridge answered {arrived} of the 2 bytes asked for at 0x{address:X}."
            );
    }

    /// <summary>
    /// Writes a 16 bit register with a request for its two bytes alone.
    /// </summary>
    public void WriteSixteen(uint address, ushort value)
    {
        Reachable(address, 1);
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        Interlocked.Increment(ref requests);
        usb.ControlOut(SetupFor(WriteRequestType, address), bytes, Patience);
    }

    public ushort ReadHalf(uint address) => (ushort)(Read(address) >> 16);

    /// <summary>
    /// Writes a 16 bit register, which is the upper half of its word; the lower half is written back as read.
    /// </summary>
    public void WriteHalf(uint address, ushort value)
    {
        using HeldTurn turn = usb.TakeTurn();
        uint word = Read(address);
        Write(address, ((uint)value << 16) | (word & 0xFFFF));
    }

    public void Modify(uint address, uint clear, uint set)
    {
        using HeldTurn turn = usb.TakeTurn();
        uint word = Read(address);
        Write(address, (word & ~clear) | set);
    }

    private void Reachable(uint address, int words)
    {
        if (!Widened && address + (uint)(words * WordBytes) > FirstAddressOutOfReachAtPowerOn)
        {
            throw new InvalidOperationException(
                $"0x{address:X} is out of the bridge's reach until its addresses are widened, and asking for it would write somewhere else."
            );
        }
    }
}
