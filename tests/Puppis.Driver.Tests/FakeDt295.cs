using System.Buffers.Binary;
using System.Security.Cryptography;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

/// <summary>
/// One I2C transfer as the bus saw it: what was written to the address, and what was read back after a repeated start.
/// </summary>
public sealed record I2cTransfer(byte Address, byte[] Written, byte[] Read)
{
    public override string ToString() =>
        $"{Address:X2} w[{Convert.ToHexString(Written)}] r[{Convert.ToHexString(Read)}]";
}

/// <summary>
/// A PIX-DT295 as its bridge answers memory requests: RAM and registers in one big-endian space, the window at
/// 0x80000, the I2C master in front of the demodulator's two halves and their tuners, the card UART with a T=1 card in
/// it, the capture ring, and the hooked write handling that runs whatever program the hook leads to on an FR30 machine.
/// </summary>
internal sealed class FakeDt295 : IUsbConnection, IFr30Memory
{
    public const uint RequestBlock = 0x000C461C;

    public const uint HookAt = CopyProgram.HookAt;

    public static readonly IReadOnlyList<uint> Sentinels =
        [0, 0x0101_0001, 0x0202_0002, 0x0303_0003, 0x0404_0004, 0x0505_0005, 0x0606_0006, 0x0707_0007, 0x0808_0008, 0x0909_0009, 0x0A0A_000A, RequestBlock, 0x0C0C_000C, 0x0D0D_000D, 0x0E0E_000E, 0x0F0F_000F];

    private readonly Dictionary<uint, uint> space = [];
    private readonly object sync = new();
    private readonly Queue<byte[]> bulk = new();
    private readonly List<byte> fifo = [];
    private readonly Queue<byte> cardOut = new();
    private readonly List<byte> cardIn = [];
    private readonly List<byte> sent = [];
    private readonly List<byte> answered = [];
    private byte busAddress;
    private bool busWriting;
    private bool busReading;
    private byte readPointer;
    private int cardSequence;
    private bool cardPowered;

    public FakeDt295()
    {
        Word(BridgeWindow.ReadWindow, BridgeWindow.AtPowerOn);
        Word(BridgeWindow.WriteWindow, BridgeWindow.AtPowerOn);
        Word(HookAt, CopyProgram.HookAtPowerOn[0]);
        Word(HookAt + 4, CopyProgram.HookAtPowerOn[1]);
        Word(CopyProgram.UsbGroup, CopyProgram.UsbGroupAtRest);
        Word(0xFDBC04, 0x85000000);
        Word(0xFDBC14, 0x000403FA);
        Word(0xFDBD08, 0x00FF6000);
        Word(0x01900404, 0x00040000);
        Word(0xF10114, 0x80000000);
        Word(0xF10118, 0x90000000);
        Word(0xF1011C, 0x479F0000);
        Word(CardUart.Status, CardUart.CardInserted);
        Demodulator[0x0E] = Dt295Frontend.DemodulatorIdentity;
        TerrestrialTuner[0x18] = Dt295Frontend.TunerIdentity;

        for (uint at = 0xE1000; at < 0xE2000; at += 4)
        {
            Word(at, 0xDEAD0000 | (at & 0xFFFF));
        }
    }

    public string Path => "/dev/bus/usb/001/005";

    public byte[] Demodulator { get; } = new byte[256];

    public byte[] Satellite { get; } = new byte[256];

    public byte[] TerrestrialTuner { get; } = new byte[256];

    public byte[] SatelliteTuner { get; } = new byte[256];

    public Queue<byte> SatelliteTunerReads { get; } = new();

    public List<I2cTransfer> Transfers { get; } = [];

    public List<string> Log { get; } = [];

    public List<(UsbSetup Setup, byte[] Data)> Writes { get; } = [];

    public List<uint> PassThroughExits { get; } = [];

    public uint[]? RegistersLeft { get; private set; }

    public bool FlagsLeftEqual { get; private set; }

    public bool Acknowledging { get; set; } = true;

    public bool Accepting { get; set; } = true;

    public bool CardPresent { get; set; } = true;

    public bool InstructionsTimeOut { get; set; }

    /// <summary>
    /// Says, for the address a write reaches, whether the request fails; the write lands first when
    /// <see cref="WritesLandBeforeFailing"/> says so.
    /// </summary>
    public Func<uint, bool>? FailingWrite { get; set; }

    public bool WritesLandBeforeFailing { get; set; }

    /// <summary>
    /// Runs before each write lands, with the address it reaches; whatever it throws is what the request throws.
    /// </summary>
    public Action<uint>? BeforeWrite { get; set; }

    /// <summary>
    /// Says, for the address a read reaches, whether the request fails.
    /// </summary>
    public Func<uint, bool>? FailingRead { get; set; }

    public long Requests { get; private set; }

    /// <summary>
    /// Whether the capture block writes one more unit each time its writer's position is read, as a broadcast keeps
    /// it moving.
    /// </summary>
    public bool Broadcasting { get; set; }

    private byte unitCounter;

    public byte[] AnswerToReset { get; set; } = [0x3B, 0xF0, 0x12, 0x00, 0xFF, 0x91, 0x81, 0xB1, 0x7C, 0x45, 0x1F, 0x03, 0x00];

    public FakeCardConnection Card { get; } = new();

    public bool ReadAnEmptyCardFifo { get; private set; }

    public bool Disposed { get; private set; }

    public bool BulkClosed { get; private set; }

    public long Instructions { get; private set; }

    public Action<string>? Sleeping { get; set; }

    public static FakeDt295 Locking()
    {
        var device = new FakeDt295();
        device.AnswerToReset[^1] = Tuning.Usb.PixDt295.AnswerToReset.Check(device.AnswerToReset.AsSpan(1, device.AnswerToReset.Length - 2));
        device.Demodulator[0x80] = 0x06;
        device.Demodulator[0xB0] = 0xA9;
        device.Demodulator[0x8B] = 0x00;
        device.Demodulator[0x8C] = 0x07;
        device.Demodulator[0x8D] = 0x56;

        return device;
    }

    /// <summary>
    /// The SHA-256 of the handler region as this fake holds it, with the hook counted as it is at power on.
    /// </summary>
    public string HandlerHash()
    {
        lock (sync)
        {
            byte[] bytes = new byte[Dt295Startup.HandlerBytes];

            for (int at = 0; at < bytes.Length; at += 4)
            {
                uint address = Dt295Startup.HandlerStart + (uint)at;
                uint word = address == HookAt ? CopyProgram.HookAtPowerOn[0]
                    : address == HookAt + 4 ? CopyProgram.HookAtPowerOn[1]
                    : ReadWord(address);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), word);
            }

            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
    }

    public uint Word(uint address)
    {
        lock (sync)
        {
            return ReadWord(address);
        }
    }

    public void Word(uint address, uint value)
    {
        lock (sync)
        {
            Store(address, value);
        }
    }

    public IReadOnlyList<(UsbSetup Setup, byte[] Data)> WritesSoFar()
    {
        lock (sync)
        {
            return [.. Writes];
        }
    }

    public IReadOnlyList<uint> WordsWrittenTo(uint address)
    {
        lock (sync)
        {
            return [.. Writes.Where(write => AddressOf(write.Setup) == address && write.Data.Length >= 4).Select(write => BinaryPrimitives.ReadUInt32BigEndian(write.Data))];
        }
    }

    /// <summary>
    /// The capture block writes <paramref name="units"/> stamped packets into the ring and moves its writer on.
    /// </summary>
    public void Broadcast(int units, byte firstCounter = 0)
    {
        lock (sync)
        {
            uint writer = ReadWord(CopyProgram.WriterPosition);

            for (int unit = 0; unit < units; unit++)
            {
                uint at = CopyProgram.RingStart + writer;
                Store(at, 0);
                Store(at + 4, 0x47000000 | (uint)((firstCounter + unit) & 0xF));

                for (uint word = 8; word < 192; word += 4)
                {
                    Store(at + word, 0);
                }

                writer = (writer + 192) % CopyProgram.RingBytes;
            }

            Store(CopyProgram.WriterPosition, writer);
        }
    }

    private uint WriteOneUnit()
    {
        uint writer = Load(CopyProgram.WriterPosition);
        uint at = CopyProgram.RingStart + writer;
        Store(at, 0);
        Store(at + 4, 0x47000000 | (uint)(unitCounter++ & 0xF));
        writer = (writer + 192) % CopyProgram.RingBytes;
        Store(CopyProgram.WriterPosition, writer);

        return writer;
    }

    public static uint AddressOf(UsbSetup setup) => ((uint)(setup.Value >> 8) << 16) | setup.Index;

    public int ControlIn(UsbSetup setup, Span<byte> data, TimeSpan timeout)
    {
        lock (sync)
        {
            Requests++;
            uint address = Reached(AddressOf(setup), BridgeWindow.ReadWindow);

            if (FailingRead?.Invoke(address) is true)
            {
                throw Failed("reading from the control endpoint");
            }

            if (data.Length is 2)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data, ReadHalf(address));

                return 2;
            }

            for (int at = 0; at < data.Length; at += 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data[at..], ReadWord(address + (uint)at));
            }

            return data.Length;
        }
    }

    public void ControlOut(UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        lock (sync)
        {
            if (InstructionsTimeOut && (byte)setup.Value is CopyProgram.InstructionMark)
            {
                throw DvbFailure.AtDevice(Path, "writing to the control endpoint", 110, "The device stopped answering the host.");
            }

            Requests++;
            Writes.Add((setup, data.ToArray()));
            uint reached = Reached(AddressOf(setup), BridgeWindow.WriteWindow);
            BeforeWrite?.Invoke(reached);
            bool failing = FailingWrite?.Invoke(reached) is true;

            if (failing && !WritesLandBeforeFailing)
            {
                throw Failed("writing to the control endpoint");
            }

            try
            {
                Land(setup, data);
            }
            finally
            {
                if (failing)
                {
                    throw Failed("writing to the control endpoint");
                }
            }
        }
    }

    private DvbDeviceException Failed(string operation) =>
        DvbFailure.AtDevice(Path, operation, 110, "The device stopped answering the host.");

    private void Land(UsbSetup setup, ReadOnlySpan<byte> data)
    {
        Stage(setup, data);

        if (Hooked())
        {
            uint exit = RunTheHook(setup, data);

            if (exit == CopyProgram.Leave)
            {
                return;
            }

            PassThroughExits.Add(exit);
        }

        uint address = Reached(AddressOf(setup), BridgeWindow.WriteWindow);

        if (data.Length is 2)
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(data);

            if (address == Dt295Startup.GpioValue && (value & Dt295Startup.DemodulatorReset) is 0)
            {
                Demodulator[0x0E] = Dt295Frontend.DemodulatorIdentity;
            }

            WriteHalf(address, value);

            return;
        }

        for (int at = 0; at < data.Length; at += 4)
        {
            WriteWord(address + (uint)at, BinaryPrimitives.ReadUInt32BigEndian(data[at..]));
        }
    }

    public IUsbBulkReader ReadBulk(byte endpoint, int transferBytes, int transfersInFlight)
    {
        lock (sync)
        {
            BulkOpened = (endpoint, transferBytes, transfersInFlight);
            BulkClosed = false;
            bulk.Clear();
            fifo.Clear();
        }

        return new Reader(this, transferBytes);
    }

    public (byte Endpoint, int Bytes, int InFlight)? BulkOpened { get; private set; }

    public Exception? DisposingThrows { get; set; }

    public void Dispose()
    {
        Disposed = true;

        if (DisposingThrows is { } failure)
        {
            throw failure;
        }
    }

    public uint ReadWord(uint address) =>
        address switch
        {
            CardUart.Received => (uint)cardOut.Count << 16,
            CardUart.ReceiveByte => TakeCardByte(),
            CardUart.Status => (CardPresent ? CardUart.CardInserted : 0) | (Load(address) & ~CardUart.CardInserted),
            CopyProgram.FifoControl => Accepting ? CopyProgram.FifoAccepts : 0,
            CopyProgram.WriterPosition when Broadcasting && Load(CopyProgram.CaptureRunning) is CopyProgram.Running => WriteOneUnit(),
            _ => Load(address),
        };

    public ushort ReadHalf(uint address) => (ushort)(Load(address & ~3u) >> (int)(16 - (8 * (address & 2))));

    public byte ReadByte(uint address) => (byte)(Load(address & ~3u) >> (int)(24 - (8 * (address & 3))));

    public void WriteWord(uint address, uint value)
    {
        Store(address, value);

        switch (address)
        {
            case BridgeI2c.Control:
                Drive(value);
                break;
            case CardUart.SendByte:
                cardIn.Add((byte)value);
                break;
            case CardUart.Transmit when value is 1:
                Answer();
                break;
            case CardUart.Power:
                cardPowered = value is 1;
                break;
            case CardUart.Line when value is 0x0E && cardPowered && CardPresent:
                cardSequence = 0;
                Enqueue(AnswerToReset);
                break;
            case CopyProgram.Fifo:
                fifo.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
                break;
            case CopyProgram.FifoControl when value is CopyProgram.FifoAccepts && Load(CopyProgram.UsbGroup) is CopyProgram.FifoGroup:
                bulk.Enqueue([.. fifo]);
                fifo.Clear();
                Monitor.PulseAll(sync);
                break;
        }
    }

    private uint Load(uint address) => space.GetValueOrDefault(address);

    private void Store(uint address, uint value) => space[address] = value;

    private void WriteHalf(uint address, ushort value)
    {
        int shift = (int)(16 - (8 * (address & 2)));
        uint word = address & ~3u;
        space[word] = (Load(word) & ~(0xFFFFu << shift)) | ((uint)value << shift);
    }

    private void WriteByte(uint address, byte value)
    {
        int shift = (int)(24 - (8 * (address & 3)));
        uint word = address & ~3u;
        space[word] = (Load(word) & ~(0xFFu << shift)) | ((uint)value << shift);
    }

    private uint Reached(uint address, uint window) =>
        address is >= BridgeWindow.Opening and < BridgeWindow.Opening + BridgeWindow.Span
            ? Load(window) + (address & (BridgeWindow.Span - 1))
            : address;

    private bool Hooked() => CopyProgram.IsOurHook(Load(HookAt), Load(HookAt + 4));

    /// <summary>
    /// Lays a write out in the block the bridge's handling reads it from, which every write passes through.
    /// </summary>
    private void Stage(UsbSetup setup, ReadOnlySpan<byte> data)
    {
        Store(RequestBlock, AddressOf(setup));

        for (int at = 0; at < 64; at++)
        {
            WriteByte(RequestBlock + 4 + (uint)at, at < data.Length ? data[at] : (byte)0);
        }

        WriteByte(RequestBlock + 0x46, (byte)setup.Value);
        WriteHalf(RequestBlock + 0x48, (ushort)data.Length);
    }

    private uint RunTheHook(UsbSetup setup, ReadOnlySpan<byte> data)
    {
        var machine = new Fr30Machine(this);
        Sentinels.ToArray().CopyTo(machine.Registers, 0);
        uint exit = machine.Run(CopyProgram.Entry, CopyProgram.Entry, CopyProgram.Entry + CopyProgram.PlaceBytes);

        if (exit == CopyProgram.Leave)
        {
            Instructions++;
        }
        else
        {
            RegistersLeft = [.. machine.Registers];
            FlagsLeftEqual = machine.Zero;
        }

        return exit;
    }

    private void Drive(uint control)
    {
        uint status = 0;
        byte data = (byte)Load(BridgeI2c.Data);

        switch (control)
        {
            case BridgeI2c.Idle:
                Finish();
                break;
            case BridgeI2c.SendAfterRepeatedStart:
                status = Begin(data);
                break;
            case BridgeI2c.SendAfterStart when !busWriting && !busReading:
                status = Begin(data);
                break;
            case BridgeI2c.SendAfterStart when busWriting:
                sent.Add(data);
                status = Acknowledging ? 0 : BridgeI2c.StatusNoAcknowledge;
                break;
            default:
                byte next = NextRead();
                answered.Add(next);
                Store(BridgeI2c.Data, next);
                break;
        }

        Store(BridgeI2c.Control, control | BridgeI2c.ControlByteDone);
        Store(BridgeI2c.Status, status);
    }

    private uint Begin(byte address)
    {
        bool known = (address & 0xFE) is Dt295Chips.Terrestrial or Dt295Chips.Satellite;

        if ((address & 1) is 0)
        {
            busAddress = address;
            busWriting = true;
            sent.Clear();
            answered.Clear();
        }
        else
        {
            busWriting = false;
            busReading = true;
            readPointer = 0;
        }

        return known && Acknowledging ? 0 : BridgeI2c.StatusNoAcknowledge;
    }

    private byte NextRead()
    {
        bool gateway = sent.Count >= 2 && sent[0] is Dt295Chips.Gateway && sent[1] is Dt295Chips.TunerRead;
        int at = readPointer++;

        if (busAddress is Dt295Chips.Terrestrial)
        {
            return gateway ? TerrestrialTuner[TerrestrialTunerPointer] : Demodulator[(sent[0] + at) & 0xFF];
        }

        if (gateway)
        {
            return SatelliteTunerReads.TryDequeue(out byte value) ? value : (byte)0;
        }

        return Satellite[(sent[0] + at) & 0xFF];
    }

    public byte TerrestrialTunerPointer { get; private set; }

    private void Finish()
    {
        if (busWriting || busReading)
        {
            Transfers.Add(new I2cTransfer(busAddress, [.. sent], [.. answered]));
            Log.Add(Transfers[^1].ToString());
        }

        if (busWriting && sent.Count > 0)
        {
            Commit();
        }

        busWriting = false;
        busReading = false;
    }

    private void Commit()
    {
        byte[] chip = busAddress is Dt295Chips.Terrestrial ? Demodulator : Satellite;
        byte[] tuner = busAddress is Dt295Chips.Terrestrial ? TerrestrialTuner : SatelliteTuner;

        if (sent[0] is not Dt295Chips.Gateway)
        {
            for (int at = 1; at < sent.Count; at++)
            {
                chip[(sent[0] + at - 1) & 0xFF] = sent[at];
            }

            return;
        }

        if (sent.Count < 3 || sent[1] is not Dt295Chips.TunerWrite)
        {
            return;
        }

        if (busAddress is Dt295Chips.Terrestrial && sent.Count is 4 && sent[2] is Dt295Chips.TunerReadPointer)
        {
            TerrestrialTunerPointer = sent[3];

            return;
        }

        for (int at = 3; at < sent.Count; at++)
        {
            tuner[(sent[2] + at - 3) & 0xFF] = sent[at];
        }
    }

    private uint TakeCardByte()
    {
        if (!cardOut.TryDequeue(out byte value))
        {
            ReadAnEmptyCardFifo = true;
        }

        return value;
    }

    private void Enqueue(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            cardOut.Enqueue(value);
        }
    }

    private readonly List<byte> command = [];

    private void Answer()
    {
        byte[] block = [.. cardIn];
        cardIn.Clear();

        if (!cardPowered || block.Length < 4)
        {
            return;
        }

        byte control = block[1];
        byte[] information = block[3..^1];

        if (control is T1Protocol.IfsRequest)
        {
            Enqueue(T1Protocol.Block(T1Protocol.IfsResponse, information));

            return;
        }

        if ((control & 0x80) is not 0)
        {
            return;
        }

        command.AddRange(information);

        if ((control & 0x20) is not 0)
        {
            Enqueue(T1Protocol.Block((byte)(0x80 | ((((control >> 6) & 1) ^ 1) << 4)), []));

            return;
        }

        byte[] reply = Card.Transmit([.. command]);
        command.Clear();
        Enqueue(T1Protocol.Block((byte)(cardSequence << 6), reply));
        cardSequence ^= 1;
    }

    private sealed class Reader(FakeDt295 device, int transferBytes) : IUsbBulkReader
    {
        private readonly List<byte> pending = [];

        public int Take(Span<byte> into, TimeSpan patience)
        {
            lock (device.sync)
            {
                if (device.BulkClosed)
                {
                    throw new ObjectDisposedException(nameof(Reader));
                }

                while (pending.Count < transferBytes && device.bulk.TryDequeue(out byte[]? packet))
                {
                    pending.AddRange(packet);
                }

                if (pending.Count < transferBytes)
                {
                    Monitor.Wait(device.sync, TimeSpan.FromMilliseconds(5));

                    return 0;
                }

                pending.GetRange(0, transferBytes).ToArray().AsSpan().CopyTo(into);
                pending.RemoveRange(0, transferBytes);

                return transferBytes;
            }
        }

        public void Dispose()
        {
            lock (device.sync)
            {
                device.BulkClosed = true;
            }
        }
    }
}
