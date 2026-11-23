using System.Text;

using Puppis.Driver.CardLending;
using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tests;

public sealed class HeadHoldLogTests
{
    private const int PmtPid = 0x0101;

    private const int OtherPmtPid = 0x0102;

    private const int EcmPid = 0x0901;

    private const int VideoPid = 0x0111;

    private const int OtherVideoPid = 0x0121;

    private const string HeldAsItStands =
        "The head of the stream was held for 940 bytes without every programme and ECM being read, so it is handed on as it stands.";

    private static readonly byte[] FirstEcm = Encoding.ASCII.GetBytes("synthetic ecm one");

    private static (TransportStreamDescrambler Descrambler, SteppedClock Clock, CapturingLogger<TransportStreamDescrambler> Log) Open(
        HeadHold hold,
        int limitInPackets = 0,
        double cardTakesMilliseconds = 42.5,
        string cardKind = "local"
    )
    {
        var clock = new SteppedClock(UsbTestSupport.Start);
        var log = new CapturingLogger<TransportStreamDescrambler>();
        FakeSmartCardService service = FakeSmartCardService.WithOneCard(out _);
        var card = new CardThatTakesItsTime(ConditionalAccessCard.Open(service), clock, TimeSpan.FromMilliseconds(cardTakesMilliseconds));

        return (
            new TransportStreamDescrambler(
                card,
                log,
                limitInPackets is 0 ? TransportStreamDescrambler.LongestHold : limitInPackets * SyntheticScrambledStream.PacketLength,
                hold,
                clock,
                cardKind
            ),
            clock,
            log
        );
    }

    private static byte[] After(SteppedClock clock, double milliseconds, SyntheticScrambledStream stream)
    {
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));

        return stream.Input;
    }

    private static string LimitWarning(CapturingLogger<TransportStreamDescrambler> log) =>
        Assert.Single(log.Lines, line => line.Contains("reached its size limit under", StringComparison.Ordinal));

    [Fact]
    public void ALiveHeadSaysWhenEachTableCameHowLongTheCardTookAndThatTheLiveConditionLetItGo()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld);

        using (descrambler)
        {
            Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid)).Input));
            Assert.Empty(descrambler.Descramble(After(clock, 10, new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]))));
            Assert.NotEmpty(descrambler.Descramble(After(clock, 20, new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm))));
        }

        Assert.Equal(
            [
                "Information The first ECM, on PID 0x0901, was answered by the local card in 42.5 ms, 72.5 ms after the first byte was received.",
                "Information The head of the stream, 564 bytes, was handed on 72.5 ms after the first byte was received under the live hold because the live condition was met: PAT after 0.0 ms, first PMT naming an ECM PID after 10.0 ms, first ECM after 30.0 ms, first answer from the card after 72.5 ms.",
            ],
            log.Lines
        );
    }

    [Fact]
    public void ARecordingHeadIsLetGoByTheEveryProgrammeCondition()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.EveryProgramme, cardKind: "borrowed");

        using (descrambler)
        {
            Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid)).Input));
            Assert.Empty(descrambler.Descramble(After(clock, 5, new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]))));
            Assert.Empty(descrambler.Descramble(After(clock, 5, new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm))));
            Assert.NotEmpty(descrambler.Descramble(After(clock, 400, new SyntheticScrambledStream().Pmt(OtherPmtPid, 2, null, [(OtherVideoPid, null)]))));
        }

        Assert.Equal(
            [
                "Information The first ECM, on PID 0x0901, was answered by the borrowed card in 42.5 ms, 52.5 ms after the first byte was received.",
                "Information The head of the stream, 752 bytes, was handed on 452.5 ms after the first byte was received under the every-programme hold because the every-programme condition was met: PAT after 0.0 ms, first PMT naming an ECM PID after 5.0 ms, first ECM after 10.0 ms, first answer from the card after 52.5 ms.",
            ],
            log.Lines
        );
    }

    [Fact]
    public void AHeadWithNothingScrambledSaysWhichTablesItNeverSaw()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.EveryProgramme);

        using (descrambler)
        {
            descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid)).Pmt(PmtPid, 1, null, [(VideoPid, null)]).Input);
        }

        Assert.Equal(
            "Information The head of the stream, 376 bytes, was handed on 0.0 ms after the first byte was received under the every-programme hold because the every-programme condition was met: PAT after 0.0 ms, first PMT naming an ECM PID not seen, first ECM not seen, first answer from the card not seen.",
            Assert.Single(log.Lines)
        );
    }

    [Fact]
    public void AHeadThatReachesItsLimitWithoutAPatSaysThePatWasNotSeen()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 5);
        SyntheticScrambledStream stream = new();

        for (int packet = 0; packet < 5; packet++)
        {
            stream.Scrambled(VideoPid, FirstEcm, odd: false, seed: packet);
        }

        using (descrambler)
        {
            descrambler.Descramble(stream.Input[..SyntheticScrambledStream.PacketLength]);
            clock.Advance(TimeSpan.FromMilliseconds(3900));
            Assert.NotEmpty(descrambler.Descramble(stream.Input[SyntheticScrambledStream.PacketLength..]));
        }

        Assert.Equal(
            [
                $"Warning {HeldAsItStands}",
                "Warning The head of the stream reached its size limit under the live hold, 3900.0 ms after the first byte was received, with this still missing: the PAT was not seen; no ECM PID is known; 5 held scrambled packets were on PIDs tied to no answered ECM (PID 0x0111).",
                "Information The head of the stream, 940 bytes, was handed on 3900.0 ms after the first byte was received under the live hold because it reached its size limit: PAT not seen, first PMT naming an ECM PID not seen, first ECM not seen, first answer from the card not seen.",
            ],
            log.Lines
        );
    }

    [Fact]
    public void AHeadThatReachesItsLimitKnowingNoEcmPidSaysSo()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 5);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, null, [(VideoPid, null)])
            .Clear(VideoPid, 1)
            .Clear(VideoPid, 2)
            .Clear(VideoPid, 3);

        using (descrambler)
        {
            Assert.NotEmpty(descrambler.Descramble(stream.Input));
        }

        string warning = LimitWarning(log);

        Assert.EndsWith("with this still missing: no ECM PID is known.", warning, StringComparison.Ordinal);
        Assert.Contains($"Warning {HeldAsItStands}", log.Lines);
    }

    [Fact]
    public void AHeadThatReachesItsLimitWaitingForAnEcmNamesThePidAndSaysNoEcmCameAndTheCardWasNotAsked()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 5);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1)
            .Scrambled(VideoPid, FirstEcm, odd: true, seed: 2)
            .Clear(OtherVideoPid, 3);

        using (descrambler)
        {
            Assert.NotEmpty(descrambler.Descramble(stream.Input));
        }

        Assert.EndsWith(
            "with this still missing: the ECM on PID 0x0901 had no answer (an ECM was not seen on it and the card was asked 0 times); 2 held scrambled packets were on PIDs tied to no answered ECM (PID 0x0111).",
            LimitWarning(log),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AHeadThatReachesItsLimitHoldingPacketsNoAnsweredEcmCoversCountsThemAndNamesTheirPids()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 6);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Scrambled(OtherVideoPid, FirstEcm, odd: false, seed: 1)
            .Ecm(EcmPid, FirstEcm)
            .Scrambled(OtherVideoPid, FirstEcm, odd: true, seed: 2)
            .Scrambled(0x0131, FirstEcm, odd: true, seed: 3);

        using (descrambler)
        {
            Assert.NotEmpty(descrambler.Descramble(stream.Input));
        }

        Assert.EndsWith(
            "with this still missing: 3 held scrambled packets were on PIDs tied to no answered ECM (PID 0x0121, 0x0131).",
            LimitWarning(log),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ARecordingHeadThatReachesItsLimitNamesThePmtItNeverSaw()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.EveryProgramme, limitInPackets: 5);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Ecm(EcmPid, FirstEcm)
            .Clear(VideoPid, 1)
            .Clear(VideoPid, 2);

        using (descrambler)
        {
            Assert.NotEmpty(descrambler.Descramble(stream.Input));
        }

        Assert.Matches(
            @"^Warning The head of the stream reached its size limit under the every-programme hold, \d+\.\d ms after the first byte was received, with this still missing: the PMT on PID 0x0102 was not seen\.$",
            LimitWarning(log)
        );
    }

    [Fact]
    public void AnEcmThatComesAfterTheHeadWentAtItsLimitIsStillTimedOnce()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 3);
        SyntheticScrambledStream head = new SyntheticScrambledStream()
            .Pat((1, PmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1);

        using (descrambler)
        {
            Assert.NotEmpty(descrambler.Descramble(head.Input));
            descrambler.Descramble(After(clock, 250, new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm)));
            descrambler.Descramble(After(clock, 100, new SyntheticScrambledStream().Ecm(EcmPid, Encoding.ASCII.GetBytes("second synthetic ecm"))));
        }

        Assert.Equal(
            "Information The first ECM, on PID 0x0901, was answered by the local card in 42.5 ms, 292.5 ms after the first byte was received.",
            Assert.Single(log.Lines, line => line.Contains("The first ECM", StringComparison.Ordinal))
        );
    }

    [Fact]
    public void NothingIsWrittenForThePacketsThatAreHeldOrForThoseThatFollowTheHead()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld);
        SyntheticScrambledStream held = new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid));
        SyntheticScrambledStream following = new();

        for (int packet = 0; packet < 400; packet++)
        {
            held.Scrambled(VideoPid, FirstEcm, odd: packet % 2 is 0, seed: packet);
            following.Scrambled(VideoPid, FirstEcm, odd: packet % 2 is 0, seed: 1000 + packet).Ecm(EcmPid, FirstEcm).Pat((1, PmtPid), (2, OtherPmtPid));
        }

        using (descrambler)
        {
            Assert.Empty(descrambler.Descramble(held.Input));
            Assert.Empty(log.Lines);

            Assert.NotEmpty(
                descrambler.Descramble(new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]).Ecm(EcmPid, FirstEcm).Input)
            );
            Assert.Equal(2, log.Lines.Count);

            Assert.NotEmpty(descrambler.Descramble(following.Input));
            Assert.Equal(2, log.Lines.Count);
        }
    }

    [Fact]
    public void NoLineCarriesAnEcmAKeyOrWhatTheCardAnswered()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld, limitInPackets: 4);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
            .Scrambled(OtherVideoPid, FirstEcm, odd: true, seed: 1)
            .Ecm(EcmPid, FirstEcm);

        using (descrambler)
        {
            descrambler.Descramble(stream.Input);
        }

        string written = string.Join('\n', log.Lines);
        string[] secrets =
        [
            Convert.ToHexString(FirstEcm),
            Encoding.ASCII.GetString(FirstEcm),
            Convert.ToHexString(SyntheticCardKeys.PairFor(FirstEcm)),
            Convert.ToHexString(SyntheticCardKeys.PairFor(FirstEcm).AsSpan(0, 8)),
            Convert.ToHexString(SyntheticCardKeys.PairFor(FirstEcm).AsSpan(8)),
            Convert.ToHexString(SyntheticCardKeys.SystemKey),
            Convert.ToHexString(SyntheticCardKeys.InitialValue),
            Convert.ToHexString(SyntheticCardKeys.CardId),
        ];

        Assert.Equal(4, log.Lines.Count);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, written, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheCardInTheReaderIsCalledLocalAndALentOneBorrowed()
    {
        var clock = new SteppedClock(UsbTestSupport.Start);
        var log = new CapturingLogger<TransportStreamDescrambler>();
        byte[] stream = new SyntheticScrambledStream().Pat((1, PmtPid)).Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]).Ecm(EcmPid, FirstEcm).Input;
        CardsOnHand lent = new();

        using (IDescrambler local = Descramblers.Over(() => FakeSmartCardService.WithOneCard(out _), log, clock).Open(HeadHold.WhatIsHeld)!)
        using (IDescrambler borrowed = BorrowedCards.Over(lent.Open, log, clock: clock).Open(HeadHold.WhatIsHeld)!)
        {
            local.Descramble(stream);
            borrowed.Descramble(stream);
        }

        Assert.Equal(
            [
                "Information The first ECM, on PID 0x0901, was answered by the local card in 0.0 ms, 0.0 ms after the first byte was received.",
                "Information The first ECM, on PID 0x0901, was answered by the borrowed card in 0.0 ms, 0.0 ms after the first byte was received.",
            ],
            log.Lines.Where(line => line.Contains("The first ECM", StringComparison.Ordinal))
        );
    }

    private sealed class CardThatTakesItsTime(IConditionalAccess card, SteppedClock clock, TimeSpan takes) : IConditionalAccess
    {
        public int CaSystemId => card.CaSystemId;

        public EcmAnswer Answer(ReadOnlySpan<byte> ecmBody)
        {
            clock.Advance(takes);

            return card.Answer(ecmBody);
        }

        public void Dispose() => card.Dispose();
    }
}
