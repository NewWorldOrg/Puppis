using Puppis.Driver.Transport;

namespace Puppis.Driver.Tuning.Usb.Bridge;

public sealed class CaptureUnpacker
{
    public const int StampBytes = 4;

    public const int UnitBytes = StampBytes + TsPacketReader.PacketLength;

    private const byte SyncByte = 0x47;

    private byte[] pending = new byte[UnitBytes * 2];
    private int held;
    private bool aligned;

    public long Realignments { get; private set; }

    public void Forget()
    {
        held = 0;
        aligned = false;
    }

    public int Unpack(ReadOnlySpan<byte> arrived, List<byte> packets)
    {
        Hold(arrived);
        int at = 0;
        int before = packets.Count;

        while (true)
        {
            if (!aligned && !TryAlign(ref at))
            {
                break;
            }

            if (held - at < UnitBytes)
            {
                break;
            }

            if (pending[at + StampBytes] != SyncByte)
            {
                aligned = false;
                Realignments++;

                continue;
            }

            packets.AddRange(pending.AsSpan(at + StampBytes, TsPacketReader.PacketLength));
            at += UnitBytes;
        }

        Drop(at);

        return packets.Count - before;
    }

    private bool TryAlign(ref int at)
    {
        for (int start = at; start + UnitBytes + StampBytes < held; start++)
        {
            if (pending[start + StampBytes] == SyncByte && pending[start + UnitBytes + StampBytes] == SyncByte)
            {
                at = start;
                aligned = true;

                return true;
            }
        }

        at = Math.Max(at, held - UnitBytes - StampBytes);

        return false;
    }

    private void Hold(ReadOnlySpan<byte> arrived)
    {
        if (pending.Length < held + arrived.Length)
        {
            Array.Resize(ref pending, Math.Max(pending.Length * 2, held + arrived.Length));
        }

        arrived.CopyTo(pending.AsSpan(held));
        held += arrived.Length;
    }

    private void Drop(int consumed)
    {
        consumed = Math.Clamp(consumed, 0, held);
        pending.AsSpan(consumed, held - consumed).CopyTo(pending);
        held -= consumed;
    }
}
