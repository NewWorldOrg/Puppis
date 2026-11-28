using System.Text;

using Puppis.Contracts;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Transport;

namespace Puppis.Driver.Tests;

public sealed class ChannelTablesTests
{
    private const int Channel = 13;

    private const int PmtPid = 0x0101;

    private const int OtherPmtPid = 0x0102;

    private const int EcmPid = 0x0901;

    private const int OtherEcmPid = 0x0902;

    private const int VideoPid = 0x0111;

    private const int OtherVideoPid = 0x0121;

    private const int PacketLength = SyntheticScrambledStream.PacketLength;

    private static readonly TablesKey Key = new(TunerKind.Terrestrial, Channel, null);

    private static readonly byte[] FirstEcm = Encoding.ASCII.GetBytes("synthetic ecm one");

    private static readonly byte[] SecondEcm = Encoding.ASCII.GetBytes("second synthetic ecm");

    [Fact]
    public void ALiveHoldNamingNoServicePutsThePatAndEveryPmtReadInFrontWithNoCounterJump()
    {
        SyntheticScrambledStream stream = WholeChannel();

        using TransportStreamDescrambler descrambler = Open(new TableMemory(), out FakeCardConnection _);
        byte[] output = descrambler.Descramble(stream.Input);

        Assert.Equal([.. InFront(stream, (0, 15), (1, 15), (2, 15)), .. stream.Output], output);
        SessionCounters counted = Counted(output);
        Assert.Equal(0, counted.Drops);
        Assert.Equal(0, counted.Duplicates);
    }

    [Fact]
    public void OnlyThePmtsReadByTheTimeTheHeadGoesArePutInFront()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1)
            .Ecm(EcmPid, FirstEcm);

        using TransportStreamDescrambler descrambler = Open(new TableMemory(), out FakeCardConnection _);

        Assert.Equal([.. InFront(stream, (0, 15), (1, 15)), .. stream.Output], descrambler.Descramble(stream.Input));
    }

    [Fact]
    public void TheHeadIsLetGoWhenEveryEcmHeldIsAnsweredAsBefore()
    {
        SyntheticScrambledStream stream = WholeChannel();
        int beforeTheLastEcm = stream.PacketCount - 1;

        using TransportStreamDescrambler descrambler = Open(new TableMemory(), out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(stream.Input[..(beforeTheLastEcm * PacketLength)]));
        Assert.NotEmpty(descrambler.Descramble(stream.Input[(beforeTheLastEcm * PacketLength)..]));
        Assert.Equal(0, descrambler.EveryProgrammeFrom);
    }

    [Fact]
    public void TheWholeChannelIsRememberedOnceEveryListedPmtHasBeenRead()
    {
        TableMemory memory = new();

        using (TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection _))
        {
            descrambler.Descramble(WholeChannel().Input);
        }

        StreamTables tables = Assert.IsType<StreamTables>(memory.Recall(Key));
        Assert.Equal([PmtPid, OtherPmtPid], tables.Pmts.Select(pmt => pmt.Pid));
        Assert.Equal([EcmPid, OtherEcmPid], tables.EcmPids);
    }

    [Fact]
    public void WithTheChannelRememberedTheCardIsAskedForEachEcmAsItPasses()
    {
        TableMemory memory = new();
        using (TransportStreamDescrambler first = Open(memory, out FakeCardConnection _))
        {
            first.Descramble(WholeChannel().Input);
        }

        SyntheticScrambledStream ecmsFirst = new SyntheticScrambledStream().Ecm(OtherEcmPid, SecondEcm).Ecm(EcmPid, FirstEcm);

        using TransportStreamDescrambler again = Open(memory, out FakeCardConnection card);

        Assert.Empty(again.Descramble(ecmsFirst.Input));
        Assert.Equal([SecondEcm, FirstEcm], card.EcmBodies);
    }

    [Theory]
    [InlineData(SessionPurpose.Recording)]
    [InlineData(SessionPurpose.Survey)]
    [InlineData(SessionPurpose.SurveyNow)]
    [InlineData(SessionPurpose.Scan)]
    [InlineData(SessionPurpose.Logo)]
    public void EveryOtherPurposePutsNoTablesInFront(SessionPurpose purpose)
    {
        SyntheticScrambledStream stream = WholeChannel();
        TableMemory memory = new();
        HeadHold hold = HeadHolds.For(purpose, new TuningRequest(TunerKind.Terrestrial, Channel), memory);

        using TransportStreamDescrambler descrambler = Opened(hold, out FakeCardConnection _);

        Assert.Equal(stream.Output, descrambler.Descramble(stream.Input));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void ALiveHeadNamingNoServiceSaysWhatWasPutInFrontAndWhetherItWasRemembered()
    {
        TableMemory memory = new();
        CapturingLogger<TransportStreamDescrambler> firstLog = new();
        CapturingLogger<TransportStreamDescrambler> secondLog = new();

        foreach (CapturingLogger<TransportStreamDescrambler> log in new[] { firstLog, secondLog })
        {
            using TransportStreamDescrambler descrambler = Logged(memory, log);
            descrambler.Descramble(WholeChannel().Input);
        }

        Assert.Equal(
            "Information The head of the stream, 1316 bytes, was handed on 0.0 ms after the first byte was received under the live hold because the every-programme condition was met, with no tables remembered from an earlier opening, and 3 packets of the PAT and every PMT were put in front of it: PAT after 0.0 ms, first PMT naming an ECM PID after 0.0 ms, first ECM after 0.0 ms, first answer from the card after 0.0 ms.",
            Assert.Single(firstLog.Lines, line => line.Contains("The head of the stream", StringComparison.Ordinal))
        );
        Assert.Contains(
            "using the tables remembered from an earlier opening, and 3 packets of the PAT and every PMT were put in front of it",
            Assert.Single(secondLog.Lines, line => line.Contains("The head of the stream", StringComparison.Ordinal)),
            StringComparison.Ordinal
        );
        Assert.Equal(
            "Information The tables remembered for Terrestrial channel 13 matched the PAT and the PMTs this stream carried.",
            secondLog.Lines.Last()
        );
    }

    private static SyntheticScrambledStream WholeChannel() =>
        new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Pmt(OtherPmtPid, 2, OtherEcmPid, [(OtherVideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1)
            .Scrambled(OtherVideoPid, SecondEcm, odd: false, seed: 2)
            .Ecm(EcmPid, FirstEcm)
            .Ecm(OtherEcmPid, SecondEcm);

    private static byte[] InFront(SyntheticScrambledStream stream, params (int Packet, int Counter)[] packets) =>
        [.. packets.SelectMany(packet => WithCounter(stream.Input.AsSpan(packet.Packet * PacketLength, PacketLength), packet.Counter))];

    private static byte[] WithCounter(ReadOnlySpan<byte> packet, int counter)
    {
        byte[] copy = packet.ToArray();
        copy[3] = (byte)((copy[3] & 0xF0) | counter);

        return copy;
    }

    private static SessionCounters Counted(byte[] output)
    {
        TsPacketReader reader = new();
        ContinuityCounterTracker tracker = new();
        foreach (TsPacket packet in reader.Read(output))
        {
            tracker.Observe(packet);
        }

        return tracker.Snapshot();
    }

    private static TransportStreamDescrambler Open(TableMemory memory, out FakeCardConnection card) =>
        Opened(HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, Channel), memory), out card);

    private static TransportStreamDescrambler Opened(HeadHold hold, out FakeCardConnection card)
    {
        FakeSmartCardService service = FakeSmartCardService.WithOneCard(out card);

        return new TransportStreamDescrambler(ConditionalAccessCard.Open(service), hold: hold);
    }

    private static TransportStreamDescrambler Logged(TableMemory memory, CapturingLogger<TransportStreamDescrambler> log)
    {
        FakeSmartCardService service = FakeSmartCardService.WithOneCard(out _);

        return new TransportStreamDescrambler(
            ConditionalAccessCard.Open(service),
            log,
            hold: HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, Channel), memory),
            time: new SteppedClock(UsbTestSupport.Start)
        );
    }
}
