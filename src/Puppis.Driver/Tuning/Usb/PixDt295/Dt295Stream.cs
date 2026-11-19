using System.Collections.Concurrent;

using Puppis.Driver.Transport;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295Stream : IDisposable
{
    public const uint Behind = CopyProgram.RingBytes / 4 * 3;

    public const int MostHeldBytes = 64 * 1024 * 1024;

    public static readonly TimeSpan InstructionPatience = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan LongestBetweenInstructions = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ReapPatience = TimeSpan.FromMilliseconds(100);

    private readonly TurnTakingConnection usb;
    private readonly BridgeMemory memory;
    private readonly Func<uint> writer;
    private readonly IUsbBulkReader reader;
    private readonly DevicePacing pacing;
    private readonly CaptureUnpacker unpacker = new();
    private readonly List<byte> unpacked = [];
    private readonly Queue<byte[]> packets = new();
    private int heldBytes;
    private int headTaken;
    private readonly ConcurrentQueue<long> breaks = new();
    private readonly object held = new();
    private readonly Thread asking;
    private readonly Thread reaping;
    private volatile bool stopping;
    private bool closed;
    private Exception? fault;
    private long skips;
    private long dropped;
    private uint sequence;

    public Dt295Stream(TurnTakingConnection usb, BridgeMemory memory, Func<uint> writer, IUsbBulkReader reader, DevicePacing pacing)
    {
        this.usb = usb;
        this.memory = memory;
        this.writer = writer;
        this.reader = reader;
        this.pacing = pacing;
        asking = new Thread(Ask) { IsBackground = true, Name = "PIX-DT295 copying" };
        reaping = new Thread(Reap) { IsBackground = true, Name = "PIX-DT295 bulk reads" };
    }

    public long Losses => Interlocked.Read(ref skips) + unpacker.Realignments + Interlocked.Read(ref dropped);

    public long Skips => Interlocked.Read(ref skips);

    public bool Faulted
    {
        get
        {
            lock (held)
            {
                return fault is not null;
            }
        }
    }

    public long BytesSent { get; private set; }

    public void Start()
    {
        reaping.Start();
        asking.Start();
    }

    public byte[] Take(int count, TimeSpan patience, CancellationToken cancellationToken)
    {
        long asked = pacing.Timestamp;

        lock (held)
        {
            while (heldBytes < TsPacketReader.PacketLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (closed)
                {
                    throw Closing(cancellationToken);
                }

                if (fault is not null)
                {
                    throw Stopped();
                }

                if (pacing.Since(asked) >= patience)
                {
                    return [];
                }

                Monitor.Wait(held, ReapPatience);
            }

            int whole = Math.Max(count / TsPacketReader.PacketLength, 1) * TsPacketReader.PacketLength;
            byte[] delivered = new byte[Math.Min(heldBytes, whole)];
            int filled = 0;

            while (filled < delivered.Length)
            {
                byte[] head = packets.Peek();
                int taking = Math.Min(head.Length - headTaken, delivered.Length - filled);
                Buffer.BlockCopy(head, headTaken, delivered, filled, taking);
                filled += taking;
                headTaken += taking;

                if (headTaken == head.Length)
                {
                    packets.Dequeue();
                    headTaken = 0;
                }
            }

            heldBytes -= delivered.Length;

            return delivered;
        }
    }

    public void Dispose()
    {
        lock (held)
        {
            closed = true;
            Monitor.PulseAll(held);
        }

        stopping = true;
        Join(asking);
        Join(reaping);
    }

    public static OperationCanceledException Closing(CancellationToken cancellationToken) =>
        new("The tuner was closed while it was being read, so the read ends here.", cancellationToken);

    private static void Join(Thread thread)
    {
        if (thread.ThreadState is not ThreadState.Unstarted)
        {
            thread.Join();
        }
    }

    public static CopyAnswer Instruct(
        TurnTakingConnection usb,
        BridgeMemory memory,
        uint number,
        CopyCommand command,
        uint position,
        uint limit
    ) =>
        usb.Together(() =>
        {
            usb.ControlOut(
                CopyProgram.InstructionSetup,
                CopyProgram.Instruction(number, command, position, limit),
                InstructionPatience
            );

            return CopyAnswer.From(memory.ReadWords(CopyProgram.Instructions, 16), number, command, usb.Path);
        });

    private void Ask()
    {
        uint position = 0;
        long previous = pacing.Timestamp;

        try
        {
            while (!stopping)
            {
                if (pacing.Since(previous) > LongestBetweenInstructions)
                {
                    position = Skip(writer());
                }

                previous = pacing.Timestamp;
                CopyAnswer answer = Instruct(usb, memory, ++sequence, CopyCommand.Copy, position, CopyProgram.MostChunks);

                if (answer.Result is not CopyResult.Good)
                {
                    throw DvbFailure.Refused(
                        $"{usb.Path}: the copying program stopped with result {answer.Result}, so the stream has to be started again."
                    );
                }

                BytesSent += answer.Copied * CopyProgram.ChunkBytes;
                position = answer.Next;

                if (Ahead(answer.Writer, position) > Behind)
                {
                    position = Skip(answer.Writer);
                }
            }
        }
        catch (Exception stopped) when (stopped is not OutOfMemoryException)
        {
            Fail(stopped);
        }
    }

    private uint Skip(uint at)
    {
        Interlocked.Increment(ref skips);
        breaks.Enqueue(BytesSent);

        return at / CopyProgram.ChunkBytes * CopyProgram.ChunkBytes % CopyProgram.RingBytes;
    }

    private static uint Ahead(uint writerAt, uint position) =>
        (writerAt + CopyProgram.RingBytes - position) % CopyProgram.RingBytes;

    private void Reap()
    {
        byte[] transfer = new byte[Dt295Capture.TransferBytes];
        long received = 0;

        try
        {
            while (!stopping)
            {
                int arrived = reader.Take(transfer, ReapPatience);

                if (arrived > 0)
                {
                    Hand(transfer.AsSpan(0, arrived), received);
                    received += arrived;
                }
            }
        }
        catch (Exception stopped) when (stopped is not OutOfMemoryException)
        {
            Fail(stopped);
        }
    }

    private void Hand(ReadOnlySpan<byte> arrived, long at)
    {
        lock (held)
        {
            while (breaks.TryPeek(out long broken) && broken < at + arrived.Length)
            {
                int before = (int)Math.Max(broken - at, 0);
                Unpack(arrived[..before]);
                unpacker.Forget();
                arrived = arrived[before..];
                at += before;
                breaks.TryDequeue(out _);
            }

            Unpack(arrived);
            Monitor.PulseAll(held);
        }
    }

    private void Unpack(ReadOnlySpan<byte> arrived)
    {
        if (arrived.IsEmpty)
        {
            return;
        }

        if (heldBytes + arrived.Length > MostHeldBytes)
        {
            Interlocked.Increment(ref dropped);
            unpacker.Forget();

            return;
        }

        unpacked.Clear();

        if (unpacker.Unpack(arrived, unpacked) > 0)
        {
            packets.Enqueue([.. unpacked]);
            heldBytes += unpacked.Count;
        }
    }

    private void Fail(Exception stopped)
    {
        lock (held)
        {
            fault ??= stopped;
            stopping = true;
            Monitor.PulseAll(held);
        }
    }

    private DvbDeviceException Stopped() =>
        fault as DvbDeviceException
        ?? DvbFailure.Refused($"{usb.Path}: the stream stopped: {fault!.Message}");
}
