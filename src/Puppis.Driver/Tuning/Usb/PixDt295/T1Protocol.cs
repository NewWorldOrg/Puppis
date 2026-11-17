using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// What a card's answer to reset says about how to speak to it: the first byte of its speed and the most a block
/// it takes may carry.
/// </summary>
public sealed record AnswerToReset(byte SpeedByte, bool OffersT1, int InformationFieldSize)
{
    public const byte Direct = 0x3B;

    public const int DefaultInformationFieldSize = 32;

    /// <summary>
    /// Reads an answer to reset, or answers null for bytes that are not a whole and well-formed one.
    /// </summary>
    public static AnswerToReset? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] is not Direct)
        {
            return null;
        }

        int at = 1;
        int historical = bytes[1] & 0x0F;
        int present = bytes[1] >> 4;
        int level = 1;
        byte speed = 0x11;
        bool t1 = false;
        bool anyButT0 = false;
        int? fieldSize = null;
        int protocol = 0;
        at++;

        while (true)
        {
            if (bytes.Length < at + int.PopCount(present))
            {
                return null;
            }

            if ((present & 0x1) is not 0)
            {
                if (level is 1)
                {
                    speed = bytes[at];
                }
                else if (level >= 3 && protocol is 1 && fieldSize is null)
                {
                    fieldSize = bytes[at];
                }

                at++;
            }

            at += int.PopCount(present & 0x6);

            if ((present & 0x8) is 0)
            {
                break;
            }

            protocol = bytes[at] & 0x0F;
            t1 |= protocol is 1;
            anyButT0 |= protocol is not 0;
            present = bytes[at] >> 4;
            at++;
            level++;
        }

        int length = at + historical + (anyButT0 ? 1 : 0);

        if (bytes.Length < length || (anyButT0 && Check(bytes[1..length]) is not 0))
        {
            return null;
        }

        return new AnswerToReset(speed, t1, fieldSize ?? DefaultInformationFieldSize);
    }

    public static byte Check(ReadOnlySpan<byte> bytes)
    {
        byte check = 0;

        foreach (byte value in bytes)
        {
            check ^= value;
        }

        return check;
    }
}

/// <summary>
/// The block protocol of ISO/IEC 7816-3 (T=1) over something that sends a frame and collects one: commands are cut to
/// what the card takes per block and chained, chained answers are acknowledged and joined, and a card asking for
/// more time is given it. A block that arrives broken ends the command rather than being asked for again.
/// </summary>
public sealed class T1Protocol(Func<byte[], byte[]> exchangeBlock, int cardFieldSize)
{
    public const byte Node = 0x00;

    public const byte HostFieldSize = 0xFE;

    public const byte IfsRequest = 0xC1;

    public const byte IfsResponse = 0xE1;

    public const byte WaitRequest = 0xC3;

    public const byte WaitResponse = 0xE3;

    private const byte More = 0x20;

    /// <summary>
    /// The most times a card may ask for more time within one command.
    /// </summary>
    public const int MostWaitRequests = 16;

    private int sent;
    private int expected;

    public static byte[] Block(byte control, ReadOnlySpan<byte> information)
    {
        byte[] block = [Node, control, (byte)information.Length, .. information, 0];
        block[^1] = AnswerToReset.Check(block.AsSpan(0, block.Length - 1));

        return block;
    }

    public static int? LengthOf(IReadOnlyList<byte> partial) => partial.Count >= 3 ? 4 + partial[2] : null;

    public static bool IsInformation(byte control) => (control & 0x80) is 0;

    public static bool IsReceiveReady(byte control) => (control & 0xC0) is 0x80;

    public void AskForBiggerBlocks()
    {
        byte[] answer = Exchange(Block(IfsRequest, [HostFieldSize]));

        if (answer[1] is not IfsResponse || answer[2] is not 1 || answer[3] is not HostFieldSize)
        {
            throw Lost("the card did not take the block size it was offered");
        }
    }

    public byte[] Transmit(ReadOnlySpan<byte> command)
    {
        byte[] answer = SendChained(command);
        List<byte> received = [];
        int waits = 0;

        while (true)
        {
            byte control = answer[1];

            if (control is WaitRequest)
            {
                if (++waits > MostWaitRequests)
                {
                    throw Lost($"the card asked for more time more than {MostWaitRequests} times in one command");
                }

                answer = Exchange(Block(WaitResponse, answer.AsSpan(3, answer[2])));

                continue;
            }

            if (!IsInformation(control) || ((control >> 6) & 1) != expected)
            {
                throw Lost($"the card answered with block 0x{control:X2} where its information was expected");
            }

            received.AddRange(answer.AsSpan(3, answer[2]));
            expected ^= 1;

            if ((control & More) is 0)
            {
                return [.. received];
            }

            answer = Exchange(Block(ReceiveReady(expected), []));
        }
    }

    private byte[] SendChained(ReadOnlySpan<byte> command)
    {
        int at = 0;

        while (true)
        {
            int taking = Math.Min(cardFieldSize, command.Length - at);
            bool last = at + taking >= command.Length;
            byte control = (byte)((sent << 6) | (last ? 0 : More));
            byte[] answer = Exchange(Block(control, command.Slice(at, taking)));
            sent ^= 1;
            at += taking;

            if (last)
            {
                return answer;
            }

            if (!IsReceiveReady(answer[1]) || ((answer[1] >> 4) & 1) != sent)
            {
                throw Lost($"the card answered a chained block with 0x{answer[1]:X2}");
            }
        }
    }

    private static byte ReceiveReady(int next) => (byte)(0x80 | (next << 4));

    private byte[] Exchange(byte[] block)
    {
        byte[] answer = exchangeBlock(block);

        if (Whole(answer))
        {
            return answer;
        }

        throw answer.Length is 0
            ? new SmartCardException("T=1: the card did not answer within its time.", SmartCardCodes.Timeout)
            : Lost($"the card's answer of {answer.Length} bytes is not a whole block");
    }

    private static bool Whole(byte[] answer) =>
        answer.Length >= 4 && answer.Length == 4 + answer[2] && AnswerToReset.Check(answer) is 0;

    private static SmartCardException Lost(string what) => new($"T=1: {what}.", SmartCardCodes.CommunicationLost);
}
