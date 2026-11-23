using System.Text.RegularExpressions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed partial class CardOpeningLogTests
{
    private const FrontendStatus Locked =
        FrontendStatus.Signal
        | FrontendStatus.Carrier
        | FrontendStatus.Viterbi
        | FrontendStatus.Sync
        | FrontendStatus.Lock;

    private static readonly DeviceSettings Tuner = new("tuner-a", DeviceKind.Terrestrial, "/dev/dvb/adapter0/frontend0");

    private static (ITunerDevice Device, CapturingLogger<TunerDeviceFactory> Log) Open(IDescramblerFactory cards, TimeProvider? clock = null)
    {
        var calls = new ScriptedDvbSystemCalls();
        var log = new CapturingLogger<TunerDeviceFactory>();
        calls.ReportStatus(Locked);
        TunerDeviceFactory factory = TunerDeviceFactory.Using(
            new DriverConfiguration(null, null, 0, new TunerSettings(TunerBackend.Dvb), null),
            clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            calls,
            cards,
            log
        );

        return (factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 27), null, HeadHold.WhatIsHeld, SessionPurpose.Live), log);
    }

    private static string CardLine(CapturingLogger<TunerDeviceFactory> log) =>
        Assert.Single(log.Lines, line => line.Contains(": asking for the card ended ", StringComparison.Ordinal));

    [Fact]
    public void ACardThatAnsweredBeforeTheTunerWasOpenIsSaidToHaveComeBeforeItAndByHowMuch()
    {
        var clock = new ClockReadDifferentlyOffTheOpeningThread(tunerOpenAfter: 370, cardAnsweredAfter: 45.5);

        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(new Cards(answers: true), clock);

        using (device)
        {
            Assert.Equal(
                "Information devices['tuner-a']: asking for the card ended 45.5 ms after the opening began, 324.5 ms before the tuner was open, and a card answered.",
                CardLine(log)
            );
        }
    }

    [Fact]
    public void ACardThatAnsweredAfterTheTunerWasOpenIsSaidToHaveComeAfterItAndByHowMuch()
    {
        var clock = new ClockReadDifferentlyOffTheOpeningThread(tunerOpenAfter: 370, cardAnsweredAfter: 1526);

        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(new Cards(answers: true), clock);

        using (device)
        {
            Assert.Equal(
                "Information devices['tuner-a']: asking for the card ended 1526.0 ms after the opening began, 1156.0 ms after the tuner was open, and a card answered.",
                CardLine(log)
            );
        }
    }

    [Fact]
    public void AnOpeningNoCardAnsweredSaysSoAndKeepsTheLineItAlwaysWrote()
    {
        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(new Cards(answers: false));

        using (device)
        {
            Assert.Matches(
                @"^Information devices\['tuner-a'\]: asking for the card ended \d+\.\d ms after the opening began, \d+\.\d ms (before|after) the tuner was open, and no card answered\.$",
                CardLine(log)
            );
            Assert.Single(log.Lines, line => InHand().IsMatch(line));
        }
    }

    [Fact]
    public void ADriverThatUnscramblesNothingWritesNoLineAboutACard()
    {
        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(NoDescrambling.Instance);

        using (device)
        {
            Assert.DoesNotContain(log.Lines, line => line.Contains("the card", StringComparison.Ordinal));
        }
    }

    [GeneratedRegex(@"^Information The tuner was open after \d+\.\d{3} s and the card was in hand after \d+\.\d{3} s\.$")]
    private static partial Regex InHand();

    private sealed class Cards(bool answers) : IDescramblerFactory
    {
        public bool Unscrambles => true;

        public CardIdentity? CardInHand => null;

        public IDescrambler? Open(HeadHold hold) => answers ? new ScriptedDescrambler(stream => stream.ToArray()) : null;
    }

    private sealed class ClockReadDifferentlyOffTheOpeningThread(double tunerOpenAfter, double cardAnsweredAfter) : TimeProvider
    {
        private readonly Thread opening = Thread.CurrentThread;
        private int readsWhileOpening;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override long GetTimestamp()
        {
            if (!ReferenceEquals(Thread.CurrentThread, opening))
            {
                return TimeSpan.FromMilliseconds(cardAnsweredAfter).Ticks;
            }

            return Interlocked.Increment(ref readsWhileOpening) is 1 ? 0 : TimeSpan.FromMilliseconds(tunerOpenAfter).Ticks;
        }
    }
}
