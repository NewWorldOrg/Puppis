using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tests;

public sealed class TableHeadTests
{
    private const int Pid = 0x0101;

    private const int PacketLength = SyntheticScrambledStream.PacketLength;

    [Fact]
    public void AShortSectionIsOnePacketStartingAUnitWithAZeroPointerAndStuffedToTheEnd()
    {
        byte[] section = [.. Enumerable.Range(0, 40).Select(value => (byte)value)];

        byte[] packets = TableHead.Packets(Pid, [section], nextCounter: 5);

        Assert.Equal(PacketLength, packets.Length);
        Assert.Equal([0x47, 0x41, 0x01, 0x14, 0x00], packets[..5]);
        Assert.Equal(section, packets[5..45]);
        Assert.All(packets[45..], value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public void ALongSectionRunsOnInPacketsThatStartNoUnitAndTheCountersEndJustBeforeTheNextOne()
    {
        byte[] section = [.. Enumerable.Range(0, 400).Select(value => (byte)value)];

        byte[] packets = TableHead.Packets(Pid, [section], nextCounter: 1);

        Assert.Equal(3, TableHead.PacketsFor([section]));
        Assert.Equal(3 * PacketLength, packets.Length);
        Assert.Equal([0x41, 0x01, 0x01], Headers(packets, 1));
        Assert.Equal([0x1E, 0x1F, 0x10], Headers(packets, 3));
        Assert.Equal(section, (byte[])[.. packets[5..PacketLength], .. packets[(PacketLength + 4)..(2 * PacketLength)], .. packets[((2 * PacketLength) + 4)..((2 * PacketLength) + 4 + 33)]]);
    }

    [Fact]
    public void EachSectionStartsAPacketOfItsOwn()
    {
        byte[] first = [0x00, 0xB0, 0x01, 0xAA];
        byte[] second = [0x00, 0xB0, 0x01, 0xBB];

        byte[] packets = TableHead.Packets(0, [first, second], nextCounter: 0);

        Assert.Equal(2 * PacketLength, packets.Length);
        Assert.Equal([0x40, 0x40], Headers(packets, 1));
        Assert.Equal([0x1E, 0x1F], Headers(packets, 3));
        Assert.Equal(second, packets[(PacketLength + 5)..(PacketLength + 9)]);
    }

    [Fact]
    public void WithNoCounterToRunIntoTheCountersStartAtZero()
    {
        byte[] section = [.. Enumerable.Range(0, 200).Select(value => (byte)value)];

        byte[] packets = TableHead.Packets(Pid, [section], nextCounter: null);

        Assert.Equal([0x10, 0x11], Headers(packets, 3));
    }

    private static byte[] Headers(byte[] packets, int index) =>
        [.. Enumerable.Range(0, packets.Length / PacketLength).Select(packet => packets[(packet * PacketLength) + index])];
}
