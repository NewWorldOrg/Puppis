using System.Text;

using Puppis.Contracts;
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

    private static readonly byte[] SecondEcm = Encoding.ASCII.GetBytes("second synthetic ecm");

    private static (TransportStreamDescrambler Descrambler, SteppedClock Clock, CapturingLogger<TransportStreamDescrambler> Log) Open(
        HeadHold hold,
        int limitInPackets = 0,
        double cardTakesMilliseconds = 42.5,
        string cardKind = "local",
        Func<int, bool>? cardIsSilentOn = null
    )
    {
        var clock = new SteppedClock(UsbTestSupport.Start);
        var log = new CapturingLogger<TransportStreamDescrambler>();
        FakeSmartCardService service = FakeSmartCardService.WithOneCard(out _);
        var card = new CardThatTakesItsTime(
            ConditionalAccessCard.Open(service),
            clock,
            TimeSpan.FromMilliseconds(cardTakesMilliseconds),
            cardIsSilentOn ?? (_ => false)
        );

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
    public void ALiveServiceHeadSaysTheServiceConditionLetItGoWithNothingRememberedAndHowManyTablePacketsWentInFront()
    {
        IReadOnlyList<string> lines = OpenTheService(new TableMemory(), [(VideoPid, null)]);

        Assert.Equal(
            [
                "Information The first ECM, on PID 0x0901, was answered by the local card in 42.5 ms, 72.5 ms after the first byte was received.",
                "Information The head of the stream, 564 bytes, was handed on 72.5 ms after the first byte was received under the service 1 hold because the service condition was met, with no tables remembered from an earlier opening, and 2 packets of the PAT and the service's PMT were put in front of it: PAT after 0.0 ms, the service's PMT after 10.0 ms, first PMT naming an ECM PID after 10.0 ms, first ECM after 30.0 ms, first answer from the card after 72.5 ms.",
            ],
            lines
        );
    }

    [Fact]
    public void ALiveServiceHeadOpenedAgainSaysItUsedTheRememberedTablesAndThatTheyMatched()
    {
        TableMemory memory = new();
        OpenTheService(memory, [(VideoPid, null)]);

        IReadOnlyList<string> lines = OpenTheService(memory, [(VideoPid, null)]);

        Assert.Equal(
            [
                "Information The first ECM, on PID 0x0901, was answered by the local card in 42.5 ms, 72.5 ms after the first byte was received.",
                "Information The head of the stream, 564 bytes, was handed on 72.5 ms after the first byte was received under the service 1 hold because the service condition was met, using the tables remembered from an earlier opening, and 2 packets of the PAT and the service's PMT were put in front of it: PAT after 0.0 ms, the service's PMT after 10.0 ms, first PMT naming an ECM PID after 10.0 ms, first ECM after 30.0 ms, first answer from the card after 72.5 ms.",
                "Information The tables remembered for service 1 on Terrestrial channel 13 matched the PAT and the PMT this stream carried.",
            ],
            lines
        );
    }

    [Fact]
    public void RememberedTablesThatDifferFromWhatTheStreamCarriedAreSaidToHaveBeenReplaced()
    {
        TableMemory memory = new();
        OpenTheService(memory, [(VideoPid, null), (OtherVideoPid, null)]);

        IReadOnlyList<string> lines = OpenTheService(memory, [(VideoPid, null)]);

        Assert.Equal(
            "Information The tables remembered for service 1 on Terrestrial channel 13 differed from what this stream carried (the PMT on PID 0x0101, version 0 remembered and 0 carried), so they were replaced by what it carried.",
            lines[^1]
        );
    }

    [Fact]
    public void ALiveServiceHeadAtItsLimitSaysWhatOfItsServiceWasStillMissing()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(new TableMemory()), limitInPackets: 3);

        using (descrambler)
        {
            descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid)).Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]).Scrambled(VideoPid, FirstEcm, odd: false, seed: 1).Input);
        }

        Assert.Equal(
            "Warning The head of the stream reached its size limit under the service 1 hold, 0.0 ms after the first byte was received, with this still missing: the ECM on PID 0x0901 had no answer (an ECM was not seen on it and the card was asked 0 times); 1 held scrambled packets of service 1 were on PIDs tied to no answered ECM (PID 0x0111).",
            LimitWarning(log)
        );
    }

    [Fact]
    public void ALiveServiceHeadAtItsLimitSaysWhenTheServicesPmtWasNotSeen()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(new TableMemory()), limitInPackets: 2);

        using (descrambler)
        {
            descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid)).Scrambled(VideoPid, FirstEcm, odd: false, seed: 1).Input);
        }

        Assert.Equal(
            "Warning The head of the stream reached its size limit under the service 1 hold, 0.0 ms after the first byte was received, with this still missing: the PMT of service 1 on PID 0x0101 was not seen.",
            LimitWarning(log)
        );
    }

    [Fact]
    public void ALiveServiceHeadAtItsLimitSaysWhenThePatDoesNotListTheService()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(new TableMemory()), limitInPackets: 2);

        using (descrambler)
        {
            descrambler.Descramble(new SyntheticScrambledStream().Pat((2, OtherPmtPid)).Scrambled(OtherVideoPid, FirstEcm, odd: false, seed: 1).Input);
        }

        Assert.Equal(
            "Warning The head of the stream reached its size limit under the service 1 hold, 0.0 ms after the first byte was received, with this still missing: the PAT does not list service 1; no ECM PID is known; 1 held scrambled packets were on PIDs tied to no answered ECM (PID 0x0121).",
            LimitWarning(log)
        );
    }

    [Fact]
    public void RememberedTablesOfAServiceThePatNoLongerListsAreSaidToHaveBeenForgotten()
    {
        TableMemory memory = new();
        OpenTheService(memory, [(VideoPid, null)]);
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(memory));

        using (descrambler)
        {
            descrambler.Descramble(new SyntheticScrambledStream().Pat((2, OtherPmtPid)).Pmt(OtherPmtPid, 2, EcmPid, [(OtherVideoPid, null)]).Ecm(EcmPid, FirstEcm).Input);
        }

        Assert.Equal(
            "Information The tables remembered for service 1 on Terrestrial channel 13 differed from what this stream carried (the PAT does not list the service), so they were forgotten.",
            log.Lines.Last()
        );
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void ALiveServiceHeadStillHeldWhenClosedSaysWhenTheServicesPmtCame()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(new TableMemory()));

        Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid)).Input));
        Assert.Empty(descrambler.Descramble(After(clock, 10, new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]))));
        clock.Advance(TimeSpan.FromMilliseconds(5));
        descrambler.Dispose();

        Assert.Equal(
            "Information The head of the stream, 376 bytes, was still held when the descrambler was closed, 15.0 ms after the first byte was received and 5.0 ms after the last, under the service 1 hold, with this still missing: the ECM on PID 0x0901 had no answer (an ECM was not seen on it and the card was asked 0 times); PAT after 0.0 ms, the service's PMT after 10.0 ms, first PMT naming an ECM PID after 10.0 ms, first ECM not seen, first answer from the card not seen.",
            Assert.Single(log.Lines)
        );
    }

    private static HeadHold ServiceHold(TableMemory memory) =>
        HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, 13, 1), memory);

    private static IReadOnlyList<string> OpenTheService(TableMemory memory, IReadOnlyList<(int Pid, int? Ecm)> streams)
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(ServiceHold(memory));

        using (descrambler)
        {
            Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid)).Input));
            Assert.Empty(descrambler.Descramble(After(clock, 10, new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, streams))));
            Assert.NotEmpty(descrambler.Descramble(After(clock, 20, new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm))));
        }

        return [.. log.Lines];
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
    public void AHeadTakenBackBecauseTheCardWentSilentSaysOnceWhatItWasStillWaitingFor()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(
            HeadHold.WhatIsHeld,
            cardIsSilentOn: _ => true
        );

        Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid)).Input));
        Assert.Empty(descrambler.Descramble(After(clock, 10, new SyntheticScrambledStream().Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]))));
        Assert.Throws<DescramblingException>(() => descrambler.Descramble(After(clock, 20, new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm))));
        clock.Advance(TimeSpan.FromMilliseconds(5));
        Assert.Equal(376, descrambler.WhatItCouldNotRead().Length);
        descrambler.Dispose();

        Assert.Equal(
            "Information The head of the stream, 376 bytes, was still held when what it held was taken back unread, 77.5 ms after the first byte was received and 47.5 ms after the last, under the live hold, with this still missing: the ECM on PID 0x0901 had no answer (an ECM was seen on it 1 times and the card was asked 1 times without answering); PAT after 0.0 ms, first PMT naming an ECM PID after 10.0 ms, first ECM after 30.0 ms, first answer from the card not seen.",
            Assert.Single(log.Lines)
        );
    }

    [Fact]
    public void AHeadStillHeldWhenTheDescramblerIsClosedSaysOnceWhatItWasStillWaitingFor()
    {
        (TransportStreamDescrambler descrambler, SteppedClock clock, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld);
        SyntheticScrambledStream held = new SyntheticScrambledStream()
            .Pat((1, PmtPid), (2, OtherPmtPid))
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1);

        Assert.Empty(descrambler.Descramble(held.Input));
        clock.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Empty(descrambler.Descramble(new SyntheticScrambledStream().Scrambled(VideoPid, FirstEcm, odd: true, seed: 2).Input));
        clock.Advance(TimeSpan.FromMilliseconds(5000));
        descrambler.Dispose();
        descrambler.Dispose();

        Assert.Equal(
            "Information The head of the stream, 564 bytes, was still held when the descrambler was closed, 5120.0 ms after the first byte was received and 5000.0 ms after the last, under the live hold, with this still missing: no ECM PID is known; 2 held scrambled packets were on PIDs tied to no answered ECM (PID 0x0111); PAT after 0.0 ms, first PMT naming an ECM PID not seen, first ECM not seen, first answer from the card not seen.",
            Assert.Single(log.Lines)
        );
    }

    [Fact]
    public void ADescramblerClosedBeforeItReceivedAByteWritesNothingAboutAHead()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld);

        Assert.Empty(descrambler.Descramble([]));
        Assert.Empty(descrambler.WhatItCouldNotRead());
        descrambler.Dispose();

        Assert.Empty(log.Lines);
    }

    [Fact]
    public void ADescramblerClosedAfterItsHeadWasHandedOnWritesNoSecondLineAboutTheHead()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(HeadHold.WhatIsHeld);
        byte[] stream = new SyntheticScrambledStream().Pat((1, PmtPid)).Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]).Ecm(EcmPid, FirstEcm).Input;

        Assert.NotEmpty(descrambler.Descramble(stream));
        descrambler.WhatItCouldNotRead();
        descrambler.Dispose();

        Assert.Single(log.Lines, line => line.Contains("The head of the stream", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("was still held", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeadHandedOnInAReadThatThenFailsIsNotReportedUntilItIsReallyHandedOnOrTakenBack()
    {
        byte[] SettledThenChanged() =>
            new SyntheticScrambledStream()
                .Pat((1, PmtPid), (2, OtherPmtPid))
                .Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)])
                .Ecm(EcmPid, FirstEcm)
                .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1)
                .Ecm(EcmPid, SecondEcm)
                .Input;

        IReadOnlyList<string> HeadLines(CapturingLogger<TransportStreamDescrambler> log) =>
            [.. log.Lines.Where(line => line.Contains("The head of the stream", StringComparison.Ordinal))];

        (TransportStreamDescrambler takenBack, _, CapturingLogger<TransportStreamDescrambler> takenBackLog) = Open(
            HeadHold.WhatIsHeld,
            cardIsSilentOn: asked => asked is 2
        );

        Assert.Throws<DescramblingException>(() => takenBack.Descramble(SettledThenChanged()));
        Assert.Empty(HeadLines(takenBackLog));
        takenBack.WhatItCouldNotRead();
        takenBack.Dispose();

        Assert.Contains("was still held when what it held was taken back unread", Assert.Single(HeadLines(takenBackLog)), StringComparison.Ordinal);

        (TransportStreamDescrambler readAgain, _, CapturingLogger<TransportStreamDescrambler> readAgainLog) = Open(
            HeadHold.WhatIsHeld,
            cardIsSilentOn: asked => asked is 2
        );

        Assert.Throws<DescramblingException>(() => readAgain.Descramble(SettledThenChanged()));
        Assert.NotEmpty(readAgain.Descramble(SettledThenChanged()));
        readAgain.Dispose();

        Assert.Contains("was handed on", Assert.Single(HeadLines(readAgainLog)), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadThatReachesItsLimitAfterTheCardWentSilentSaysTheEcmCameAndTheCardWasAskedWithoutAnswering()
    {
        (TransportStreamDescrambler descrambler, _, CapturingLogger<TransportStreamDescrambler> log) = Open(
            HeadHold.EveryProgramme,
            limitInPackets: 5,
            cardIsSilentOn: _ => true
        );
        SyntheticScrambledStream untilTheLimit = new SyntheticScrambledStream().Clear(VideoPid, 1).Clear(VideoPid, 2).Clear(VideoPid, 3);

        using (descrambler)
        {
            Assert.Empty(
                descrambler.Descramble(new SyntheticScrambledStream().Pat((1, PmtPid), (2, OtherPmtPid)).Pmt(PmtPid, 1, EcmPid, [(VideoPid, null)]).Input)
            );
            Assert.Throws<DescramblingException>(() => descrambler.Descramble(new SyntheticScrambledStream().Ecm(EcmPid, FirstEcm).Input));
            Assert.Throws<DescramblingException>(() => descrambler.Descramble(new SyntheticScrambledStream().Ecm(EcmPid, SecondEcm).Input));
            Assert.NotEmpty(descrambler.Descramble(untilTheLimit.Input));
        }

        Assert.EndsWith(
            "with this still missing: the PMT on PID 0x0102 was not seen; the ECM on PID 0x0901 had no answer (an ECM was seen on it 2 times and the card was asked 2 times without answering).",
            LimitWarning(log),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(log.Lines, line => line.Contains("was still held", StringComparison.Ordinal));
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

    private sealed class CardThatTakesItsTime(IConditionalAccess card, SteppedClock clock, TimeSpan takes, Func<int, bool> isSilentOn)
        : IConditionalAccess
    {
        private int asked;

        public int CaSystemId => card.CaSystemId;

        public EcmAnswer Answer(ReadOnlySpan<byte> ecmBody)
        {
            clock.Advance(takes);

            if (isSilentOn(++asked))
            {
                throw new DescramblingException("The synthetic card did not answer.", DescramblingFailure.CardSilent);
            }

            return card.Answer(ecmBody);
        }

        public void Dispose() => card.Dispose();
    }
}
