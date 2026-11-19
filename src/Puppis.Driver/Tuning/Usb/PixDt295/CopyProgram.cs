using System.Buffers.Binary;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public static class CopyProgram
{
    public const uint Entry = 0xE1000;

    public const uint PlaceBytes = 0x1000;

    public const uint HookAt = 0xEA6FC;

    public const uint HookJump = 0x970C9FA0;

    public const uint Leave = 0xEA8C8;

    public const uint OriginalModify = 0xEA704;

    public const uint OriginalWrite = 0xEA75A;

    public const uint Instructions = 0x000C4620;

    public const byte InstructionMark = 0x7E;

    public const int InstructionBytes = 64;

    public const uint Magic = 0x44543935;

    public const uint Answered = 0x80000000;

    public const uint RingStart = 0x8138EC00;

    public const uint RingBytes = 0x00600000;

    public const uint ChunkBytes = 512;

    public const uint MostChunks = 2048;

    public const uint Budget = 256_000;

    public const uint UsbGroup = 0xFD814C;

    public const uint UsbGroupAtRest = 15;

    public const uint FifoGroup = 8;

    public const uint FifoControl = 0xFDBD0C;

    public const uint FifoAccepts = 4;

    public const uint Fifo = 0xFDBD84;

    public const uint CaptureRunning = 0x01902010;

    public const uint Running = 0x02000000;

    public const uint WriterPosition = 0x01902028;

    public const uint ReaderPosition = 0x0190202C;

    public static readonly IReadOnlyList<uint> HookAtPowerOn = [0xC46D02B0, 0xA810E32B];

    private const int Value = 0;
    private const int Scratch = 1;
    private const int Pointer = 2;
    private const int Copied = 3;
    private const int Countdown = 4;
    private const int Left = 5;
    private const int Position = 6;
    private const int Answer = 7;
    private const int Block = 11;
    private const int Compared = 12;
    private const int Index = 13;

    private const uint ByteOfValue = 0x46;
    private const uint LengthAt = 0x48;
    private const uint DataAt = 0x04;
    private const uint CommandAt = 0x08;
    private const uint PositionAt = 0x0C;
    private const uint LimitAt = 0x10;
    private const uint WriterAt = 0x14;
    private const uint ResultAt = 0x18;
    private const uint FirstZeroAt = 0x1C;
    private const uint ZeroWords = 9;

    public static IReadOnlyList<uint> Hook => [Fr30Assembly.LoadTwentyWord(Entry, Compared), HookJump];

    public static bool IsOurHook(uint first, uint second) => first == Hook[0] && second == Hook[1];

    public static Fr30Program Build()
    {
        Fr30Assembly program = new(Index);
        program
            .Load(ByteOfValue, Index)
            .LoadByteIndexed(Block, Value)
            .Load(InstructionMark, Compared)
            .Compare(Compared, Value)
            .Branch(Fr30Condition.NotEqual, "original")
            .Load(LengthAt, Index)
            .LoadHalfIndexed(Block, Value)
            .Load(InstructionBytes, Compared)
            .Compare(Compared, Value)
            .Branch(Fr30Condition.NotEqual, "original")
            .LoadWord(Block, Value)
            .Load(Instructions, Compared)
            .Compare(Compared, Value)
            .Branch(Fr30Condition.NotEqual, "original");

        program
            .Move(Compared, Answer)
            .Move(Block, Pointer)
            .AddSmall(DataAt, Pointer)
            .Move(Answer, Copied)
            .Load(InstructionBytes / 4, Countdown)
            .Label("take")
            .LoadWord(Pointer, Value)
            .StoreWord(Value, Copied)
            .AddSmall(4, Pointer)
            .AddSmall(4, Copied)
            .SubtractSmall(1, Countdown)
            .Branch(Fr30Condition.NotEqual, "take")
            .Load(0, Copied)
            .Load(PositionAt, Index)
            .LoadWordIndexed(Answer, Position)
            .Load(0, Value)
            .Load(WriterAt, Index)
            .StoreWordIndexed(Value, Answer);

        program
            .LoadWord(Answer, Value)
            .Load(Magic, Scratch)
            .Compare(Scratch, Value)
            .Branch(Fr30Condition.NotEqual, "wrong")
            .Load(CommandAt, Index)
            .LoadWordIndexed(Answer, Value)
            .CompareSmall((uint)CopyCommand.Answer, Value)
            .Branch(Fr30Condition.Equal, "good")
            .CompareSmall((uint)CopyCommand.Copy, Value)
            .Branch(Fr30Condition.NotEqual, "wrong")
            .Load(RingBytes, Scratch)
            .Compare(Scratch, Position)
            .Branch(Fr30Condition.NoCarry, "wrong")
            .Load(ChunkBytes - 1, Value)
            .And(Position, Value)
            .Branch(Fr30Condition.NotEqual, "wrong")
            .Load(LimitAt, Index)
            .LoadWordIndexed(Answer, Left)
            .CompareSmall(0, Left)
            .Branch(Fr30Condition.Equal, "wrong")
            .Load(MostChunks, Value)
            .Compare(Value, Left)
            .Branch(Fr30Condition.Higher, "wrong")
            .Load(UsbGroup, Compared)
            .LoadWord(Compared, Value)
            .CompareSmall(UsbGroupAtRest, Value)
            .Branch(Fr30Condition.NotEqual, "wrong")
            .Load(CaptureRunning, Scratch)
            .LoadWord(Scratch, Value)
            .Load(Running, Scratch)
            .Compare(Scratch, Value)
            .Branch(Fr30Condition.NotEqual, "idle");

        program
            .Load(Budget, Countdown)
            .Label("look")
            .SubtractSmall(1, Countdown)
            .Branch(Fr30Condition.Equal, "good")
            .Load(WriterPosition, Scratch)
            .LoadWord(Scratch, Pointer)
            .Load(WriterAt, Index)
            .StoreWordIndexed(Pointer, Answer)
            .Load(RingBytes, Scratch)
            .Compare(Scratch, Pointer)
            .Branch(Fr30Condition.NoCarry, "outside")
            .Move(Pointer, Value)
            .Subtract(Position, Value)
            .Branch(Fr30Condition.NoCarry, "ahead")
            .Add(Scratch, Value)
            .Label("ahead")
            .Load(ChunkBytes, Compared)
            .Compare(Compared, Value)
            .Branch(Fr30Condition.NoCarry, "enough")
            .CompareSmall(0, Copied)
            .Branch(Fr30Condition.NotEqual, "good")
            .Branch(Fr30Condition.Always, "look")
            .Label("enough")
            .Load(FifoControl, Compared)
            .LoadWord(Compared, Value)
            .Load(FifoAccepts, Scratch)
            .And(Scratch, Value)
            .Branch(Fr30Condition.Equal, "look")
            .Load(RingStart, Pointer)
            .Add(Position, Pointer)
            .Load(Fifo, Compared)
            .Load(ChunkBytes / 4, Scratch)
            .Label("chunk")
            .LoadWord(Pointer, Value)
            .StoreWord(Value, Compared)
            .AddSmall(4, Pointer)
            .SubtractSmall(1, Scratch)
            .Branch(Fr30Condition.NotEqual, "chunk")
            .Load(UsbGroup, Compared)
            .Load(FifoGroup, Value)
            .StoreWord(Value, Compared)
            .Load(FifoControl, Scratch)
            .Load(FifoAccepts, Value)
            .StoreWord(Value, Scratch)
            .Load(UsbGroupAtRest, Value)
            .StoreWord(Value, Compared)
            .Load(ChunkBytes, Value)
            .Add(Value, Position)
            .Load(RingBytes, Scratch)
            .Compare(Scratch, Position)
            .Branch(Fr30Condition.NotEqual, "inside")
            .Load(0, Position)
            .Label("inside")
            .Load(ReaderPosition, Scratch)
            .StoreWord(Position, Scratch)
            .AddSmall(1, Copied)
            .SubtractSmall(1, Left)
            .Branch(Fr30Condition.NotEqual, "look");

        program
            .Label("good")
            .Load((uint)CopyResult.Good, Value)
            .Branch(Fr30Condition.Always, "answer")
            .Label("wrong")
            .Load((uint)CopyResult.Wrong, Value)
            .Branch(Fr30Condition.Always, "answer")
            .Label("outside")
            .Load((uint)CopyResult.WriterOutsideTheRing, Value)
            .Branch(Fr30Condition.Always, "answer")
            .Label("idle")
            .Load((uint)CopyResult.NotStreaming, Value)
            .Label("answer")
            .Load(ResultAt, Index)
            .StoreWordIndexed(Value, Answer)
            .Load(PositionAt, Index)
            .StoreWordIndexed(Position, Answer)
            .Load(LimitAt, Index)
            .StoreWordIndexed(Copied, Answer)
            .Load(CommandAt, Index)
            .LoadWordIndexed(Answer, Value)
            .Load(Answered, Scratch)
            .Or(Scratch, Value)
            .StoreWordIndexed(Value, Answer)
            .Move(Answer, Pointer)
            .Load(FirstZeroAt, Value)
            .Add(Value, Pointer)
            .Load(0, Value)
            .Load(ZeroWords, Scratch)
            .Label("clear")
            .StoreWord(Value, Pointer)
            .AddSmall(4, Pointer)
            .SubtractSmall(1, Scratch)
            .Branch(Fr30Condition.NotEqual, "clear")
            .LoadTwenty(Leave, Compared)
            .Jump(Compared);

        program
            .Label("original")
            .Load(ByteOfValue, Index)
            .LoadByteIndexed(Block, Value)
            .CompareSmall(1, Value)
            .Branch(Fr30Condition.Equal, "modify")
            .LoadTwenty(OriginalWrite, Compared)
            .Jump(Compared)
            .Label("modify")
            .LoadTwenty(OriginalModify, Compared)
            .Jump(Compared);

        return program.Assemble(Entry);
    }

    public static void Install(BridgeMemory memory, Fr30Program program, Func<CopyAnswer> ping)
    {
        uint[] hook = memory.ReadWords(HookAt, 2);
        bool ours = IsOurHook(hook[0], hook[1]);

        if (!ours && (hook[0] != HookAtPowerOn[0] || hook[1] != HookAtPowerOn[1]))
        {
            throw BridgeWindow.Unplug(memory.Path, $"the bridge's write handling at 0x{HookAt:X} holds 0x{hook[0]:X8} 0x{hook[1]:X8}");
        }

        bool inPlace = ours && memory.ReadWords(Entry, program.Words.Count).SequenceEqual(program.Words);

        if (!inPlace)
        {
            if (ours)
            {
                Unhook(memory);
            }

            Place(memory, program);
        }

        try
        {
            if (!inPlace)
            {
                memory.WriteInOneRequest(HookAt, [Hook[0], Hook[1]]);
            }

            uint[] placed = memory.ReadWords(HookAt, 2);

            if (!IsOurHook(placed[0], placed[1]))
            {
                throw DvbFailure.Refused(
                    $"{memory.Path}: the hook read back as 0x{placed[0]:X8} 0x{placed[1]:X8} after it was written."
                );
            }

            Ping(memory, ping);
        }
        catch (DvbDeviceException)
        {
            TryToUnhook(memory);

            throw;
        }
    }

    private static bool TryToUnhook(BridgeMemory memory)
    {
        try
        {
            Unhook(memory);

            return true;
        }
        catch (DvbDeviceException)
        {
            return false;
        }
    }

    private static void Place(BridgeMemory memory, Fr30Program program)
    {
        for (int word = 0; word < program.Words.Count; word++)
        {
            uint at = Entry + (uint)(4 * word);
            memory.Write(at, program.Words[word]);
            uint found = memory.Read(at);

            if (found != program.Words[word])
            {
                throw DvbFailure.Refused(
                    $"{memory.Path}: the copying program read back 0x{found:X8} at 0x{at:X} where 0x{program.Words[word]:X8} was written, so the bridge's write handling is left as it was."
                );
            }
        }
    }

    public static void Unhook(BridgeMemory memory) => memory.WriteInOneRequest(HookAt, [HookAtPowerOn[0], HookAtPowerOn[1]]);

    public static void UnhookIfOurs(BridgeMemory memory)
    {
        uint[] hook = memory.ReadWords(HookAt, 2);

        if (IsOurHook(hook[0], hook[1]))
        {
            Unhook(memory);
        }
    }

    public static byte[] Instruction(uint sequence, CopyCommand command, uint position, uint limit)
    {
        byte[] bytes = new byte[InstructionBytes];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)command);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), position);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), limit);

        return bytes;
    }

    public static UsbSetup InstructionSetup =>
        new(
            BridgeMemory.WriteRequestType,
            BridgeMemory.MemoryRequest,
            (ushort)((((Instructions >> 16) & 0xFF) << 8) | InstructionMark),
            (ushort)(Instructions & 0xFFFF)
        );

    private static void Ping(BridgeMemory memory, Func<CopyAnswer> ping)
    {
        CopyAnswer answer = ping();

        if (answer.Result is not CopyResult.Good)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: the copying program answered the request to answer with result {answer.Result}."
            );
        }
    }
}

public enum CopyCommand : uint
{
    Answer = 1,

    Copy = 2,
}

public enum CopyResult : uint
{
    Good = 0,

    Wrong = 1,

    WriterOutsideTheRing = 2,

    NotStreaming = 3,
}

public sealed record CopyAnswer(uint Next, uint Copied, uint Writer, CopyResult Result)
{
    public static CopyAnswer From(ReadOnlySpan<uint> words, uint sequence, CopyCommand command, string path)
    {
        if (words[0] != CopyProgram.Magic || words[1] != sequence || words[2] != ((uint)command | CopyProgram.Answered))
        {
            throw DvbFailure.Refused(
                $"{path}: the copying program's answer (0x{words[0]:X8}, {words[1]}, 0x{words[2]:X8}) is not the answer to instruction {sequence}."
            );
        }

        return new CopyAnswer(words[3], words[4], words[5], (CopyResult)words[6]);
    }
}
