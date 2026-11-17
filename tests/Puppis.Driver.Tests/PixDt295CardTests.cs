using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class AnswerToResetTests
{
    private static byte[] Whole(params byte[] bytes)
    {
        byte[] whole = [.. bytes, 0];
        whole[^1] = AnswerToReset.Check(whole.AsSpan(1, whole.Length - 2));

        return whole;
    }

    [Fact]
    public void TheCardsAnswerOffersT1AtItsSpeedWithTheBlockSizeOfItsThirdLevel()
    {
        AnswerToReset? answer = AnswerToReset.Read(Whole(0x3B, 0xF0, 0x12, 0x00, 0xFF, 0x91, 0x81, 0xB1, 0x7C, 0x45, 0x1F, 0x03));

        Assert.Equal(new AnswerToReset(0x12, true, 0x7C), answer);
    }

    [Fact]
    public void AnAnswerCutShortOrWithAWrongCheckIsNotRead()
    {
        byte[] whole = Whole(0x3B, 0xF0, 0x12, 0x00, 0xFF, 0x91, 0x81, 0xB1, 0x7C, 0x45, 0x1F, 0x03);

        Assert.Null(AnswerToReset.Read(whole[..^1]));
        whole[^1] ^= 0x01;
        Assert.Null(AnswerToReset.Read(whole));
        Assert.Null(AnswerToReset.Read([0x3F, 0x00]));
        Assert.Null(AnswerToReset.Read([]));
    }

    [Fact]
    public void ACardThatOffersOnlyT0SaysSoAndCarriesNoCheck()
    {
        Assert.Equal(new AnswerToReset(0x11, false, 32), AnswerToReset.Read([0x3B, 0x02, 0x14, 0x50]));
    }
}

public sealed class T1ProtocolTests
{
    [Fact]
    public void ABlockEndsWithTheExclusiveOrOfEverythingBeforeIt()
    {
        Assert.Equal([0x00, 0xC1, 0x01, 0xFE, 0x3E], T1Protocol.Block(T1Protocol.IfsRequest, [0xFE]));
        Assert.Equal([0x00, 0xE1, 0x01, 0xFE, 0x1E], T1Protocol.Block(T1Protocol.IfsResponse, [0xFE]));
    }

    [Fact]
    public void TheHostAsksForBlocksOfTwoHundredAndFiftyFourBytes()
    {
        List<byte[]> sent = [];
        var protocol = new T1Protocol(block => { sent.Add(block); return T1Protocol.Block(T1Protocol.IfsResponse, [0xFE]); }, 0x7C);

        protocol.AskForBiggerBlocks();

        Assert.Equal([[0x00, 0xC1, 0x01, 0xFE, 0x3E]], sent);
    }

    [Fact]
    public void ACommandGoesInAnInformationBlockAndItsSequenceAlternates()
    {
        List<byte[]> sent = [];
        int card = 0;
        var protocol = new T1Protocol(
            block =>
            {
                sent.Add(block);
                byte[] answer = T1Protocol.Block((byte)(card << 6), [0x90, 0x00]);
                card ^= 1;

                return answer;
            },
            0x7C
        );

        Assert.Equal([0x90, 0x00], protocol.Transmit([0x90, 0x30, 0x00, 0x00, 0x00]));
        Assert.Equal([0x90, 0x00], protocol.Transmit([0x90, 0x30, 0x00, 0x00, 0x00]));

        Assert.Equal(T1Protocol.Block(0x00, [0x90, 0x30, 0x00, 0x00, 0x00]), sent[0]);
        Assert.Equal(T1Protocol.Block(0x40, [0x90, 0x30, 0x00, 0x00, 0x00]), sent[1]);
    }

    [Fact]
    public void ACommandLongerThanTheCardTakesIsChained()
    {
        List<byte[]> sent = [];
        var protocol = new T1Protocol(
            block =>
            {
                sent.Add(block);

                return (block[1] & 0x20) is not 0
                    ? T1Protocol.Block((byte)(0x80 | ((((block[1] >> 6) & 1) ^ 1) << 4)), [])
                    : T1Protocol.Block(0x00, [0x90, 0x00]);
            },
            4
        );

        protocol.Transmit([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        Assert.Equal(T1Protocol.Block(0x20, [1, 2, 3, 4]), sent[0]);
        Assert.Equal(T1Protocol.Block(0x60, [5, 6, 7, 8]), sent[1]);
        Assert.Equal(T1Protocol.Block(0x00, [9, 10]), sent[2]);
    }

    [Fact]
    public void AChainedAnswerIsAcknowledgedAndJoined()
    {
        Queue<byte[]> answers = new([T1Protocol.Block(0x20, [0x01, 0x02]), T1Protocol.Block(0x40, [0x90, 0x00])]);
        List<byte[]> sent = [];
        var protocol = new T1Protocol(block => { sent.Add(block); return answers.Dequeue(); }, 0x7C);

        Assert.Equal([0x01, 0x02, 0x90, 0x00], protocol.Transmit([0x90, 0x34]));
        Assert.Equal(T1Protocol.Block(0x90, []), sent[1]);
    }

    [Fact]
    public void ACardAskingForMoreTimeIsGivenIt()
    {
        Queue<byte[]> answers = new([T1Protocol.Block(T1Protocol.WaitRequest, [0x05]), T1Protocol.Block(0x00, [0x90, 0x00])]);
        List<byte[]> sent = [];
        var protocol = new T1Protocol(block => { sent.Add(block); return answers.Dequeue(); }, 0x7C);

        Assert.Equal([0x90, 0x00], protocol.Transmit([0x90, 0x34]));
        Assert.Equal(T1Protocol.Block(T1Protocol.WaitResponse, [0x05]), sent[1]);
    }

    [Fact]
    public void SilenceIsATimeoutAndABrokenBlockIsLost()
    {
        var silent = new T1Protocol(_ => [], 0x7C);
        byte[] broken = T1Protocol.Block(0x00, [0x90, 0x00]);
        broken[^1] ^= 0xFF;
        var garbled = new T1Protocol(_ => broken, 0x7C);

        Assert.Equal(SmartCardCodes.Timeout, Assert.Throws<SmartCardException>(() => silent.Transmit([0x90])).Code);
        Assert.Equal(SmartCardCodes.CommunicationLost, Assert.Throws<SmartCardException>(() => garbled.Transmit([0x90])).Code);
    }

    [Fact]
    public void AnAnswerOutOfSequenceIsLost()
    {
        var protocol = new T1Protocol(_ => T1Protocol.Block(0x40, [0x90, 0x00]), 0x7C);

        Assert.Equal(SmartCardCodes.CommunicationLost, Assert.Throws<SmartCardException>(() => protocol.Transmit([0x90])).Code);
    }
}

public sealed class Dt295CardSlotTests
{
    private static (Dt295CardSlot Slot, FakeDt295 Device, List<string> Seen) Slot()
    {
        FakeDt295 device = FakeDt295.Locking();
        List<string> seen = [];
        var clock = new ManualTimeProvider(UsbTestSupport.Start);
        var pacing = new DevicePacing(clock, span => { seen.Add($"wait {span.TotalMilliseconds:0}"); clock.Advance(span); });
        var uart = new CardUart(new BridgeMemory(new RecordingConnection(device, seen)), pacing);

        return (new Dt295CardSlot(uart), device, seen);
    }

    private static IReadOnlyList<string> Steps(IReadOnlyList<(uint Address, uint Value, int Milliseconds)> table) =>
        [.. table.SelectMany(step => (IEnumerable<string>)(step.Milliseconds > 0
            ? [$"{step.Address:X6} {step.Value:X8}", $"wait {step.Milliseconds}"]
            : [$"{step.Address:X6} {step.Value:X8}"]))];

    [Fact]
    public void TheCardIsPoweredByTheNineteenStepsAndSpokenToFasterAfterItsAnswer()
    {
        (Dt295CardSlot slot, FakeDt295 device, List<string> seen) = Slot();

        using ConditionalAccessCard card = ConditionalAccessCard.Open(slot.Service());

        Assert.Equal(SyntheticCardKeys.CaSystemId, card.CaSystemId);
        Assert.Equal(
            Steps(
            [
                (0xFEA018, 0, 0), (0xFEA034, 0, 0), (0xFEA01C, 0x3FF, 0), (0xFEA004, 0, 0), (0xFEA014, 0, 0),
                (0xFEA000, 0, 20), (0xFEA000, 7, 20), (0xFEA004, 8, 0), (0xFEA008, 0xBA7F, 20), (0xFEA004, 0x0A, 0),
                (0xFEA000, 0, 20), (0xFEA000, 7, 0), (0xFEA03C, 0x3B, 0), (0xFEA020, 9600, 0), (0xFEA024, 0, 0),
                (0xFEA018, 0, 0), (0xFEA014, 0x20, 50), (0xFEA034, 1, 0), (0xFEA004, 0x0E, 0),
            ]),
            seen.Take(24)
        );
        Assert.Equal(
            ["FEA008 00005D5D", "FEA010 00010000", "FEA020 0000006B", "FEA024 00008ECF"],
            seen.Where(line => !line.StartsWith("wait", StringComparison.Ordinal)).Skip(19).Take(4)
        );
        Assert.True(slot.Powered);
        Assert.False(device.ReadAnEmptyCardFifo);
        Assert.Equal([0x90, 0x30, 0x00, 0x00, 0x00], device.Card.Commands[0]);
    }

    [Fact]
    public void AFrameIsSentByteByByteAndThenReleased()
    {
        (Dt295CardSlot slot, FakeDt295 _, List<string> seen) = Slot();
        using ConditionalAccessCard card = ConditionalAccessCard.Open(slot.Service());
        seen.Clear();

        card.Answer([0x01, 0x02, 0x03]);

        List<string> writes = [.. seen.Where(line => !line.StartsWith("wait", StringComparison.Ordinal))];
        Assert.Equal(["FEA03C 00000000", "FEA038 00000000"], writes[..2]);
        Assert.Equal(["FEA01C 000003FF", "FEA010 00000200", "FEA00C 00000001"], writes[^3..]);
        Assert.Equal(13, writes.Count(line => line.StartsWith("FEA028", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnEmptySlotIsSaidAndRefused()
    {
        (Dt295CardSlot slot, FakeDt295 device, _) = Slot();
        device.CardPresent = false;
        ISmartCardService service = slot.Service();

        Assert.Equal([CardSlot.Empty], service.Slots(service.Readers()));
        Assert.Equal(SmartCardCodes.NoSmartCard, Assert.Throws<SmartCardException>(() => service.Connect(Dt295CardSlot.ReaderName)).Code);
        Assert.Throws<DescramblingException>(() => ConditionalAccessCard.Open(slot.Service()));
    }

    [Fact]
    public void ACardThatGivesNoAnswerToResetWithinHalfASecondIsAFailure()
    {
        (Dt295CardSlot slot, FakeDt295 device, _) = Slot();
        device.AnswerToReset = [];

        Assert.Equal(SmartCardCodes.Timeout, Assert.Throws<SmartCardException>(() => slot.Service().Connect(Dt295CardSlot.ReaderName)).Code);
    }

    [Fact]
    public void ACardAskingForAnotherSpeedIsRefused()
    {
        (Dt295CardSlot slot, FakeDt295 device, _) = Slot();
        byte[] other = [0x3B, 0xF0, 0x13, 0x00, 0xFF, 0x91, 0x81, 0xB1, 0x7C, 0x45, 0x1F, 0x03, 0x00];
        other[^1] = AnswerToReset.Check(other.AsSpan(1, other.Length - 2));
        device.AnswerToReset = other;

        Assert.Equal(SmartCardCodes.CommunicationLost, Assert.Throws<SmartCardException>(() => slot.Service().Connect(Dt295CardSlot.ReaderName)).Code);
    }

    [Fact]
    public void ClosingSwitchesTheCardOffByTheSeventeenStepsAndRefusesTheSlotAfter()
    {
        (Dt295CardSlot slot, FakeDt295 _, List<string> seen) = Slot();
        ISmartCardService service = slot.Service();
        service.Connect(Dt295CardSlot.ReaderName);
        seen.Clear();

        slot.Close();

        Assert.Equal(
            Steps(
            [
                (0xFEA014, 0, 0), (0xFEA000, 3, 0), (0xFEA004, 0, 0), (0xFEA018, 0, 0), (0xFEA034, 0, 1),
                (0xFEA018, 0, 0), (0xFEA034, 0, 0), (0xFEA004, 4, 0), (0xFEA014, 0x20, 0), (0xFEA000, 7, 0),
                (0xFEA008, 0xBA7F, 1), (0xFEA03C, 0, 0), (0xFEA020, 0, 0), (0xFEA024, 0, 0), (0xFEA00C, 0, 0),
                (0xFEA010, 0x200, 0), (0xFEA038, 0, 0),
            ]),
            seen
        );
        Assert.False(slot.Powered);
        Assert.Equal([CardSlot.Empty], service.Slots(service.Readers()));
        Assert.Throws<ObjectDisposedException>(() => service.Connect(Dt295CardSlot.ReaderName));
    }
}
