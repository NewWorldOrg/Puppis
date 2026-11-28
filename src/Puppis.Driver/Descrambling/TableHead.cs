namespace Puppis.Driver.Descrambling;

public static class TableHead
{
    private const byte SyncByte = 0x47;

    private const int PacketLength = 188;

    private const int HeaderLength = 4;

    private const int PayloadLength = PacketLength - HeaderLength;

    private const byte Stuffing = 0xFF;

    private const byte UnitStart = 0x40;

    private const byte PayloadOnly = 0x10;

    private const int CounterMask = 0x0F;

    public static int PacketsFor(IEnumerable<ReadOnlyMemory<byte>> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        return sections.Sum(section => (section.Length + 1 + PayloadLength - 1) / PayloadLength);
    }

    public static byte[] Packets(int pid, IReadOnlyList<ReadOnlyMemory<byte>> sections, int? nextCounter)
    {
        ArgumentNullException.ThrowIfNull(sections);

        int count = PacketsFor(sections);
        int counter = (nextCounter ?? count) - count;
        List<byte> packets = new(count * PacketLength);

        foreach (ReadOnlyMemory<byte> section in sections)
        {
            byte[] carried = [0x00, .. section.Span];

            for (int offset = 0; offset < carried.Length; offset += PayloadLength)
            {
                packets.AddRange(Header(pid, offset is 0, counter++));
                packets.AddRange(Payload(carried.AsSpan(offset, Math.Min(PayloadLength, carried.Length - offset))));
            }
        }

        return [.. packets];
    }

    private static byte[] Header(int pid, bool unitStart, int counter) =>
        [
            SyncByte,
            (byte)((unitStart ? UnitStart : 0) | ((pid >> 8) & 0x1F)),
            (byte)(pid & 0xFF),
            (byte)(PayloadOnly | (counter & CounterMask)),
        ];

    private static byte[] Payload(ReadOnlySpan<byte> part) =>
        [.. part, .. Enumerable.Repeat(Stuffing, PayloadLength - part.Length)];
}
