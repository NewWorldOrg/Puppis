using System.Text;

using Puppis.Contracts;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Transport;

namespace Puppis.Driver.Tests;

public sealed class ServiceHeadHoldTests
{
    private const int Channel = 13;

    private const int Service = 1;

    private const int OtherService = 2;

    private const int PmtPid = 0x0101;

    private const int OtherPmtPid = 0x0102;

    private const int EcmPid = 0x0901;

    private const int OtherEcmPid = 0x0902;

    private const int VideoPid = 0x0111;

    private const int AudioPid = 0x0112;

    private const int OtherVideoPid = 0x0121;

    private const int PacketLength = SyntheticScrambledStream.PacketLength;

    private static readonly ServiceTablesKey Key = new(TunerKind.Terrestrial, Channel, Service);

    private static readonly byte[] FirstEcm = Encoding.ASCII.GetBytes("synthetic ecm one");

    private static readonly byte[] SecondEcm = Encoding.ASCII.GetBytes("second synthetic ecm");

    [Fact]
    public void AServiceHoldLetsGoOnceTheEcmItsPmtNamesIsAnsweredWithoutWaitingForAnotherProgramme()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out int headPackets);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);

        Assert.Equal(
            [.. InFront(stream, (0, 15), (1, 15)), .. Head(stream.Output, headPackets)],
            descrambler.Descramble(Head(stream.Input, headPackets))
        );
        Assert.Equal(After(stream.Output, headPackets), descrambler.Descramble(After(stream.Input, headPackets)));
    }

    [Fact]
    public void TheSameHeadUnderALiveHoldWithNoServiceStillWaitsForTheOtherProgrammeAndPutsNothingInFront()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out int headPackets, theOtherIsHandedOnScrambled: false);

        using TransportStreamDescrambler descrambler = OpenWithout(HeadHold.WhatIsHeld, out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, headPackets)));
        Assert.Equal(stream.Output, descrambler.Descramble(After(stream.Input, headPackets)));
    }

    [Fact]
    public void AServiceHoldWaitsForItsOwnPmtEvenOnceAnotherProgrammeCouldBeRead()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(OtherPmtPid, OtherService, OtherEcmPid, [(OtherVideoPid, null)])
            .Scrambled(OtherVideoPid, SecondEcm, odd: false, seed: 10)
            .Ecm(OtherEcmPid, SecondEcm);
        int headPackets = stream.PacketCount;
        stream
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 11)
            .Ecm(EcmPid, FirstEcm);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, headPackets)));
        Assert.Equal([.. InFront(stream, (0, 15), (4, 15)), .. stream.Output], descrambler.Descramble(After(stream.Input, headPackets)));
    }

    [Fact]
    public void AServiceWhosePmtNamesNoEcmIsLetGoAsSoonAsItsPmtIsRead()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .LeftScrambled(OtherVideoPid, SecondEcm, odd: false, seed: 20)
            .Pmt(PmtPid, Service, null, [(VideoPid, null)])
            .Clear(VideoPid, 21);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection card);

        Assert.Equal([.. InFront(stream, (0, 15), (2, 15)), .. stream.Output], descrambler.Descramble(stream.Input));
        Assert.Empty(card.EcmBodies);
    }

    [Fact]
    public void AServiceThePatDoesNotListIsHeldTheWayALiveHoldIsWithOnlyThePatInFrontAndIsForgotten()
    {
        ServiceTableMemory memory = new();
        Remembered(memory, EcmPid);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((OtherService, OtherPmtPid))
            .Pmt(OtherPmtPid, OtherService, OtherEcmPid, [(OtherVideoPid, null)])
            .Scrambled(OtherVideoPid, SecondEcm, odd: false, seed: 30)
            .Ecm(OtherEcmPid, SecondEcm);

        using TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection _);

        Assert.Equal([.. InFront(stream, (0, 15)), .. stream.Output], descrambler.Descramble(stream.Input));
        Assert.Null(memory.Recall(Key));
    }

    [Fact]
    public void ThePmtInFrontIsEveryPacketItsSectionTakesAndTheCountersRunIntoThePacketsAfterThemWithoutAJump()
    {
        (int Pid, int? Ecm)[] streams = [.. Enumerable.Range(0, 24).Select(index => (VideoPid + index, (int?)EcmPid))];
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .RepeatCounter(0)
            .RepeatCounter(0)
            .RepeatCounter(0)
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .RepeatCounter(PmtPid)
            .Pmt(PmtPid, Service, null, streams)
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 40)
            .Ecm(EcmPid, FirstEcm);
        stream.Pat((Service, PmtPid), (OtherService, OtherPmtPid)).Pmt(PmtPid, Service, null, streams);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);
        byte[] output = descrambler.Descramble(stream.Input);

        Assert.Equal([.. InFront(stream, (0, 12), (1, 13), (2, 14)), .. stream.Output], output);
        SessionCounters counted = Counted(output);
        Assert.Equal(0, counted.Drops);
        Assert.Equal(0, counted.Duplicates);
        Assert.Equal(stream.PacketCount + 3, counted.Packets);
    }

    [Fact]
    public void WhatIsHandedOnIsTheSameHoweverTheReadsAreCut()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out _);
        byte[] whole;
        using (TransportStreamDescrambler once = Open(new ServiceTableMemory(), out FakeCardConnection _))
        {
            whole = once.Descramble(stream.Input);
        }

        foreach (int chunk in new[] { 1, 7, 188, 500 })
        {
            using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);
            List<byte> output = [];
            for (int offset = 0; offset < stream.Input.Length; offset += chunk)
            {
                output.AddRange(descrambler.Descramble(stream.Input.AsSpan(offset, Math.Min(chunk, stream.Input.Length - offset))));
            }

            Assert.Equal(whole, output);
        }
    }

    [Fact]
    public void AnOpeningRemembersThePatThePmtAndTheEcmPidsOfItsServiceUnderItsTunerKindChannelAndService()
    {
        ServiceTableMemory memory = new();
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out _);

        using (TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection _))
        {
            descrambler.Descramble(stream.Input);
        }

        ServiceTables tables = Assert.IsType<ServiceTables>(memory.Recall(Key));
        Assert.Equal(SectionIn(stream.Input, 0), Assert.Single(tables.Pat).ToArray());
        Assert.Equal(SectionIn(stream.Input, 1), tables.Pmt.ToArray());
        Assert.Equal(PmtPid, tables.PmtPid);
        Assert.Equal([EcmPid], tables.EcmPids);
        Assert.Null(memory.Recall(Key with { PhysicalChannel = Channel + 1 }));
        Assert.Null(memory.Recall(Key with { Kind = TunerKind.Satellite }));
        Assert.Null(memory.Recall(Key with { ServiceId = OtherService }));
    }

    [Fact]
    public void AnOpeningThatIsNotLetGoRemembersNothing()
    {
        ServiceTableMemory memory = new();
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 50);

        using (TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection _))
        {
            Assert.Empty(descrambler.Descramble(stream.Input));
        }

        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void WithRememberedTablesTheCardIsAskedForTheEcmAsItPassesBeforeThePatAndThePmt()
    {
        ServiceTableMemory memory = new();
        Remembered(memory, EcmPid);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 60)
            .Ecm(EcmPid, FirstEcm);
        int ecmPackets = stream.PacketCount;
        stream
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null), (AudioPid, null)])
            .Scrambled(AudioPid, FirstEcm, odd: true, seed: 61);

        using TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection card);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, ecmPackets)));
        Assert.Equal([FirstEcm], card.EcmBodies);
        Assert.Equal(
            [.. InFront(stream, (2, 15), (3, 15)), .. stream.Output],
            descrambler.Descramble(After(stream.Input, ecmPackets))
        );
        Assert.Equal([FirstEcm], card.EcmBodies);
    }

    [Fact]
    public void WithNothingRememberedTheCardIsNotAskedUntilThePmtNamesTheEcm()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 60)
            .Ecm(EcmPid, FirstEcm);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection card);

        Assert.Empty(descrambler.Descramble(stream.Input));
        Assert.Empty(card.EcmBodies);
    }

    [Fact]
    public void ARememberedEcmPidThatNoLongerCarriesTheServiceNeverUnscramblesItAndTheMemoryIsReplaced()
    {
        ServiceTableMemory memory = new();
        Remembered(memory, OtherEcmPid);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Ecm(OtherEcmPid, SecondEcm)
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 70)
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: true, seed: 71);
        int beforeItsEcm = stream.PacketCount;
        stream.Ecm(EcmPid, FirstEcm).Scrambled(VideoPid, FirstEcm, odd: false, seed: 72);

        using TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection card);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, beforeItsEcm)));
        Assert.Equal(
            [.. InFront(stream, (2, 15), (3, 15)), .. stream.Output],
            descrambler.Descramble(After(stream.Input, beforeItsEcm))
        );
        Assert.Equal([SecondEcm, FirstEcm], card.EcmBodies);
        Assert.Equal([EcmPid], Assert.IsType<ServiceTables>(memory.Recall(Key)).EcmPids);
    }

    [Fact]
    public void ARememberedEcmPidIsListenedToOnlyWhileTheHeadIsHeld()
    {
        ServiceTableMemory memory = new();
        Remembered(memory, OtherEcmPid);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Ecm(EcmPid, FirstEcm)
            .Ecm(OtherEcmPid, SecondEcm);

        using TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection card);

        Assert.NotEmpty(descrambler.Descramble(stream.Input));
        Assert.Equal([FirstEcm], card.EcmBodies);
    }

    [Fact]
    public void AnAbandonedHeadPutsNoTablesInFrontOfWhatItGivesBack()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 80);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(stream.Input));
        Assert.Equal(stream.Input, descrambler.WhatItCouldNotRead());
    }

    [Fact]
    public void AHeadThatReachesItsLimitStillPutsWhatIsKnownInFront()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .LeftScrambled(VideoPid, FirstEcm, odd: false, seed: 90)
            .LeftScrambled(VideoPid, FirstEcm, odd: true, seed: 91);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _, holdLimit: 4 * PacketLength);

        Assert.Equal([.. InFront(stream, (0, 15), (1, 15)), .. stream.Output], descrambler.Descramble(stream.Input));
    }

    [Fact]
    public void EveryProgrammeIsReadableOnlyFromThePacketAtWhichTheLastProgrammeCanBeRead()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out int headPackets);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);

        byte[] head = descrambler.Descramble(Head(stream.Input, headPackets));
        Assert.Equal(head.Length, descrambler.EveryProgrammeFrom);
        descrambler.Descramble(After(stream.Input, headPackets));
        Assert.Equal(PacketLength, descrambler.EveryProgrammeFrom);
        descrambler.Descramble(new SyntheticScrambledStream().Clear(0x0130, 1).Input);
        Assert.Equal(0, descrambler.EveryProgrammeFrom);
    }

    [Fact]
    public void UnderTheEveryProgrammeHoldAllThatIsHandedOnIsReadableByEveryProgramme()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out _, theOtherIsHandedOnScrambled: false);

        using TransportStreamDescrambler descrambler = OpenWithout(HeadHold.EveryProgramme, out FakeCardConnection _);

        Assert.Equal(stream.Output, descrambler.Descramble(stream.Input));
        Assert.Equal(0, descrambler.EveryProgrammeFrom);
    }

    [Fact]
    public void OnceAsMuchAsTheHoldMayKeepHasPassedEveryProgrammeCountsAsReadableAsTheEveryProgrammeHoldWouldHaveLetItGo()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Ecm(EcmPid, FirstEcm)
            .LeftScrambled(OtherVideoPid, SecondEcm, odd: false, seed: 100)
            .LeftScrambled(OtherVideoPid, SecondEcm, odd: true, seed: 101);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _, holdLimit: 4 * PacketLength);

        Assert.Equal([.. InFront(stream, (0, 15), (1, 15)), .. stream.Output], descrambler.Descramble(stream.Input));
        Assert.Equal(5 * PacketLength, descrambler.EveryProgrammeFrom);
    }

    [Fact]
    public void ADescramblerOpenedPartWayThroughTheStreamPutsNoTablesInFront()
    {
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out _);
        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, Channel, Service), new ServiceTableMemory());

        using TransportStreamDescrambler descrambler = OpenWithout(hold.FromTheMiddle(), out FakeCardConnection _);

        Assert.Equal(stream.Output, descrambler.Descramble(stream.Input));
    }

    [Fact]
    public void APmtThatMovesToAnotherPidIsWaitedForAgainAndTheOldOneIsNotPutInFront()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Pat((Service, OtherPmtPid))
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 110)
            .Ecm(EcmPid, FirstEcm);
        int beforeTheNewPmt = stream.PacketCount;
        stream.Pmt(OtherPmtPid, Service, EcmPid, [(VideoPid, null), (AudioPid, null)]);

        using TransportStreamDescrambler descrambler = Open(new ServiceTableMemory(), out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, beforeTheNewPmt)));
        Assert.Equal(
            [.. WithCounter(stream.Input.AsSpan(2 * PacketLength, PacketLength), 15), .. InFront(stream, (5, 15)), .. stream.Output],
            descrambler.Descramble(After(stream.Input, beforeTheNewPmt))
        );
    }

    [Theory]
    [InlineData(SessionPurpose.Recording)]
    [InlineData(SessionPurpose.Survey)]
    [InlineData(SessionPurpose.SurveyNow)]
    [InlineData(SessionPurpose.Scan)]
    [InlineData(SessionPurpose.Logo)]
    public void EveryOtherPurposeWithAServiceIsHeldAndHandedOnAsBefore(SessionPurpose purpose)
    {
        ServiceTableMemory memory = new();
        SyntheticScrambledStream stream = WithAnotherProgrammeStillToCome(out int headPackets, theOtherIsHandedOnScrambled: false);
        HeadHold hold = HeadHolds.For(purpose, new TuningRequest(TunerKind.Terrestrial, Channel, Service), memory);

        using TransportStreamDescrambler descrambler = OpenWithout(hold, out FakeCardConnection _);

        Assert.Empty(descrambler.Descramble(Head(stream.Input, headPackets)));
        Assert.Equal(stream.Output, descrambler.Descramble(After(stream.Input, headPackets)));
        Assert.Equal(0, memory.Count);
    }

    private static SyntheticScrambledStream WithAnotherProgrammeStillToCome(out int headPackets, bool theOtherIsHandedOnScrambled = true)
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null), (AudioPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1);
        stream = theOtherIsHandedOnScrambled
            ? stream.LeftScrambled(OtherVideoPid, SecondEcm, odd: false, seed: 2)
            : stream.Scrambled(OtherVideoPid, SecondEcm, odd: false, seed: 2);
        stream
            .Ecm(EcmPid, FirstEcm)
            .Scrambled(AudioPid, FirstEcm, odd: true, seed: 3);
        headPackets = stream.PacketCount;

        return stream
            .Pmt(OtherPmtPid, OtherService, OtherEcmPid, [(OtherVideoPid, null)])
            .Ecm(OtherEcmPid, SecondEcm)
            .Scrambled(OtherVideoPid, SecondEcm, odd: true, seed: 4)
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 5);
    }

    private static void Remembered(ServiceTableMemory memory, int ecmPid)
    {
        SyntheticScrambledStream earlier = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, ecmPid, [(VideoPid, null)])
            .Ecm(ecmPid, FirstEcm);

        using TransportStreamDescrambler descrambler = Open(memory, out FakeCardConnection _);
        Assert.NotEmpty(descrambler.Descramble(earlier.Input));
        Assert.NotNull(memory.Recall(Key));
    }

    private static byte[] InFront(SyntheticScrambledStream stream, params (int Packet, int Counter)[] packets) =>
        [.. packets.SelectMany(packet => WithCounter(stream.Input.AsSpan(packet.Packet * PacketLength, PacketLength), packet.Counter))];

    private static byte[] WithCounter(ReadOnlySpan<byte> packet, int counter)
    {
        byte[] copy = packet.ToArray();
        copy[3] = (byte)((copy[3] & 0xF0) | counter);

        return copy;
    }

    private static byte[] SectionIn(byte[] stream, int packet)
    {
        ReadOnlySpan<byte> payload = stream.AsSpan((packet * PacketLength) + 5, PacketLength - 5);
        int length = 3 + (((payload[1] & 0x0F) << 8) | payload[2]);

        return payload[..length].ToArray();
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

    private static byte[] Head(byte[] stream, int packets) => stream[..(packets * PacketLength)];

    private static byte[] After(byte[] stream, int packets) => stream[(packets * PacketLength)..];

    private static TransportStreamDescrambler Open(
        ServiceTableMemory memory,
        out FakeCardConnection card,
        int holdLimit = TransportStreamDescrambler.LongestHold
    ) =>
        OpenWithout(
            HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, Channel, Service), memory),
            out card,
            holdLimit
        );

    private static TransportStreamDescrambler OpenWithout(
        HeadHold hold,
        out FakeCardConnection card,
        int holdLimit = TransportStreamDescrambler.LongestHold
    )
    {
        FakeSmartCardService service = FakeSmartCardService.WithOneCard(out card);

        return new TransportStreamDescrambler(ConditionalAccessCard.Open(service), holdLimit: holdLimit, hold: hold);
    }
}
