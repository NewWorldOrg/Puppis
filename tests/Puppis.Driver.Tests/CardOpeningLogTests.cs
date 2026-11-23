using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class CardOpeningLogTests
{
    private const FrontendStatus Locked =
        FrontendStatus.Signal
        | FrontendStatus.Carrier
        | FrontendStatus.Viterbi
        | FrontendStatus.Sync
        | FrontendStatus.Lock;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly DeviceSettings Tuner = new("tuner-a", DeviceKind.Terrestrial, "/dev/dvb/adapter0/frontend0");

    private static (ITunerDevice Device, CapturingLogger<TunerDeviceFactory> Log) Open(
        IDescramblerFactory cards,
        Action<ScriptedDvbSystemCalls>? script = null
    )
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = new ScriptedDvbSystemCalls(clock);
        var log = new CapturingLogger<TunerDeviceFactory>();
        calls.ReportStatus(Locked);
        script?.Invoke(calls);
        TunerDeviceFactory factory = TunerDeviceFactory.Using(
            new DriverConfiguration(null, null, 0, new TunerSettings(TunerBackend.Dvb), null),
            clock,
            calls,
            cards,
            log
        );

        return (factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 27), null, HeadHold.WhatIsHeld, SessionPurpose.Live), log);
    }

    private static string CardLine(CapturingLogger<TunerDeviceFactory> log) =>
        Assert.Single(log.Lines, line => line.Contains(": asking for the card ended ", StringComparison.Ordinal));

    [Fact]
    public void ACardThatAnswersWhileTheTunerIsStillLockingIsSaidToHaveComeBeforeIt()
    {
        var cards = new SignallingCards(answers: true);

        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(
            cards,
            calls =>
            {
                calls.ReportStatusesInTurn(FrontendStatus.Signal);
                calls.Resting = () => Assert.True(cards.Asked.Wait(Patience));
            }
        );

        using (device)
        {
            Assert.Matches(
                @"^Information devices\['tuner-a'\]: asking for the card ended \d+\.\d ms after the opening began, \d+\.\d ms before the tuner was open, and a card answered\.$",
                CardLine(log)
            );
        }
    }

    [Fact]
    public void ACardThatAnswersAfterTheTunerIsOpenIsSaidToHaveComeAfterIt()
    {
        using var tunerOpened = new ManualResetEventSlim();
        var cards = new SignallingCards(answers: true, before: () => Assert.True(tunerOpened.Wait(Patience)));
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = new ScriptedDvbSystemCalls(clock);
        var log = new CapturingLogger<TunerDeviceFactory>();
        var signalling = new SignalWhenTheTunerLineIsWritten(log, tunerOpened);
        calls.ReportStatus(Locked);
        TunerDeviceFactory factory = TunerDeviceFactory.Using(
            new DriverConfiguration(null, null, 0, new TunerSettings(TunerBackend.Dvb), null),
            clock,
            calls,
            cards,
            signalling
        );

        using ITunerDevice device = factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, 27), null, HeadHold.WhatIsHeld, SessionPurpose.Live);

        Assert.Matches(
            @"^Information devices\['tuner-a'\]: asking for the card ended \d+\.\d ms after the opening began, \d+\.\d ms after the tuner was open, and a card answered\.$",
            CardLine(log)
        );
    }

    [Fact]
    public void AnOpeningNoCardAnsweredSaysSoAndKeepsTheLineItAlwaysWrote()
    {
        (ITunerDevice device, CapturingLogger<TunerDeviceFactory> log) = Open(new SignallingCards(answers: false));

        using (device)
        {
            Assert.EndsWith("the tuner was open, and no card answered.", CardLine(log), StringComparison.Ordinal);
            Assert.Single(
                log.Lines,
                line => System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"^Information The tuner was open after \d+\.\d{3} s and the card was in hand after \d+\.\d{3} s\.$"
                )
            );
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

    private sealed class SignallingCards(bool answers, Action? before = null) : IDescramblerFactory
    {
        public ManualResetEventSlim Asked { get; } = new();

        public bool Unscrambles => true;

        public CardIdentity? CardInHand => null;

        public IDescrambler? Open(HeadHold hold)
        {
            before?.Invoke();
            Asked.Set();

            return answers ? new ScriptedDescrambler(stream => stream.ToArray()) : null;
        }
    }

    private sealed class SignalWhenTheTunerLineIsWritten(CapturingLogger<TunerDeviceFactory> inner, ManualResetEventSlim written)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            inner.Log(logLevel, eventId, state, exception, formatter);

            if (formatter(state, exception).Contains("opened and locked in ", StringComparison.Ordinal))
            {
                written.Set();
            }
        }
    }
}
