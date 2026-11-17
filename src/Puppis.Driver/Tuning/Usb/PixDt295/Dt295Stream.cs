using System.Collections.Concurrent;

using Puppis.Driver.Transport;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The stream of an open PIX-DT295. One thread asks the copying program for what the ring holds, one instruction after
/// another; another takes the bulk reads as they finish and unpacks them, so that the endpoint always has reads waiting
/// whatever the reader of the packets is doing. When the program falls too far behind, or too long passes between two
/// instructions, reading moves to where the writer stands and the gap is counted.
/// </summary>
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
    private Exception? fault;
    private long skips;
    private long dropped;
    private uint sequence;

    /// <param name="writer">Reads where the capture block's writer stands, for a jump past what was lost.</param>
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

    /// <summary>
    /// Times the stream lost bytes: jumps past what the ring no longer held, unit heads lost and found again, and
    /// bytes dropped because nobody took them.
    /// </summary>
    public long Losses => Interlocked.Read(ref skips) + unpacker.Realignments + Interlocked.Read(ref dropped);

    public long Skips => Interlocked.Read(ref skips);

    public long BytesSent { get; private set; }

    public void Start()
    {
        reaping.Start();
        asking.Start();
    }

    /// <summary>
    /// Takes whole packets, waiting as long as <paramref name="patience"/> by the device's clock for the first of
    /// them. Answers an empty array when none came in time; throws what stopped the stream.
    /// </summary>
    public byte[] Take(int count, TimeSpan patience, CancellationToken cancellationToken)
    {
        long asked = pacing.Timestamp;

        lock (held)
        {
            while (heldBytes < TsPacketReader.PacketLength)
            {
                if (fault is not null)
                {
                    throw Stopped();
                }

                if (pacing.Since(asked) >= patience)
                {
                    return [];
                }

                cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Stops asking, waits for the instruction in flight to come back, and then stops taking the bulk reads, which
    /// stay on the endpoint for whoever cancels them.
    /// </summary>
    public void Dispose()
    {
        stopping = true;
        Join(asking);
        Join(reaping);
    }

    private static void Join(Thread thread)
    {
        if (thread.ThreadState is not ThreadState.Unstarted)
        {
            thread.Join();
        }
    }

    /// <summary>
    /// Sends the copying program one instruction and reads its answer in the same turn, since the next write anybody
    /// sends lands where the answer is.
    /// </summary>
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
