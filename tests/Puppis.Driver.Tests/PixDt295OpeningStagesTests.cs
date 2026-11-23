using System.Globalization;
using System.Text.RegularExpressions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed partial class Dt295OpeningStagesTests
{
    private const string Path = "/dev/bus/usb/001/005";

    private const string Counted = @"\d+\.\d ms/\d+ requests/\d+\.\d ms in \d+ sleeps";

    private static readonly DeviceSettings Tuner = new("usb1-2", DeviceKind.Terrestrial, Path);

    private static (TunerDeviceFactory Factory, FakeDt295 Fake, CapturingLogger<TunerDeviceFactory> Log) Factory(FakeDt295? device = null)
    {
        FakeDt295 fake = device ?? FakeDt295.Locking();
        fake.Broadcasting = true;
        (DevicePacing pacing, _) = PixDt295Support.Pacing(fake);
        var log = new CapturingLogger<TunerDeviceFactory>();

        return (
            TunerDeviceFactory.Using(
                new DriverConfiguration(
                    OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                    Tuner: new TunerSettings(TunerBackend.Usb),
                    Devices: [Tuner]
                ),
                new FakeDt295Host(fake),
                pacing,
                new CardsThatAreNotInTheSlot(),
                fake.HandlerHash(),
                log
            ),
            fake,
            log
        );
    }

    private static ITunerDevice Live(TunerDeviceFactory factory, int channel) =>
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, channel), null, HeadHold.WhatIsHeld, SessionPurpose.Live);

    private static IReadOnlyList<string> StagesLines(CapturingLogger<TunerDeviceFactory> log) =>
        [.. log.Lines.Where(line => line.Contains("(PIX-DT295, ", StringComparison.Ordinal))];

    private static IReadOnlyList<string> StageNames(string line) =>
        [.. StageName().Matches(line[line.IndexOf('(', line.IndexOf(" ms in all", StringComparison.Ordinal))..]).Select(match => match.Groups[1].Value)];

    [Fact]
    public void TheFirstOpeningSaysOnceHowLongTheBridgeTheTuneAndTheStreamTook()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();

        using (Live(factory, 22))
        {
            string line = Assert.Single(StagesLines(log));

            Assert.StartsWith(
                "Information devices['usb1-2'] (PIX-DT295, terrestrial) for live on physical channel 22: opened and streaming in ",
                line,
                StringComparison.Ordinal
            );
            Assert.Equal(
                [
                    "USB open",
                    "handler check",
                    "demodulator reset",
                    "clocks and I2C",
                    "chip initialisation",
                    "copy program",
                    "tune registers",
                    "lock",
                    "capture set-up",
                    "endpoint set-up",
                    "bulk reads",
                    "intake",
                    "copying start",
                    "signal report",
                ],
                StageNames(line)
            );
            Assert.Matches(@"demodulator reset 25\.0 ms/\d+ requests/25\.0 ms in 2 sleeps", line);
            Assert.Matches(@"tune registers 140\.0 ms/\d+ requests/140\.0 ms in 3 sleeps", line);
            Assert.Matches(@"lock 100\.0 ms/\d+ requests/100\.0 ms in 1 sleeps/1 looks", line);
            Assert.Matches($@"signal report {Counted}\)\.$", line);
        }

        factory.Dispose();
    }

    [Fact]
    public void AnOpeningOfAKeptTunerChecksTheBridgeInsteadOfStartingIt()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();
        Live(factory, 22).Dispose();

        using (Live(factory, 27))
        {
            IReadOnlyList<string> lines = StagesLines(log);

            Assert.Equal(2, lines.Count);
            Assert.Contains("for live on physical channel 27: opened and streaming in ", lines[1], StringComparison.Ordinal);
            Assert.Equal(
                [
                    "kept bridge check",
                    "tune registers",
                    "lock",
                    "capture set-up",
                    "endpoint set-up",
                    "bulk reads",
                    "intake",
                    "copying start",
                    "signal report",
                ],
                StageNames(lines[1])
            );
            Assert.Matches(@"kept bridge check 0\.0 ms/[1-9]\d* requests/0\.0 ms in 0 sleeps", lines[1]);
        }

        factory.Dispose();
    }

    [Fact]
    public void TheExistingSummaryLineIsStillWrittenAsItWas()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();

        using (Live(factory, 22))
        {
            Assert.Single(
                log.Lines,
                line => Summary().IsMatch(line)
            );
        }

        factory.Dispose();
    }

    [Fact]
    public void ATunerThatDoesNotLockSaysHowOftenItLookedBeforeGivingUp()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory(new FakeDt295());

        Assert.Throws<DvbDeviceException>(() => Live(factory, 22));

        string line = Assert.Single(StagesLines(log));

        Assert.Contains("for live on physical channel 22: gave up without a lock after ", line, StringComparison.Ordinal);
        Assert.Matches(@"lock 3000\.0 ms/\d+ requests/3000\.0 ms in 30 sleeps/30 looks\)\.$", line);
        factory.Dispose();
    }

    [Fact]
    public void TheRequestsOfTheTuneAreTheOnesTheBridgeWasSent()
    {
        FakeDt295 fake = FakeDt295.Locking();
        (DevicePacing pacing, SteppedClock clock) = PixDt295Support.Pacing(fake);
        Dt295Bridge bridge = Dt295Bridge.Open(new FakeDt295Host(fake), Path, pacing, null, fake.HandlerHash());
        var stages = new StageTimes(clock);
        bridge.Count(stages);
        long before = fake.Requests;

        Assert.True(bridge.Frontend.TuneTerrestrial(BroadcastStandards.TerrestrialCentreHz(27), stages));

        long counted = Requests().Matches(stages.ToString()).Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));

        Assert.Equal(fake.Requests - before, counted);
        Assert.Equal(["tune registers", "lock"], StageNames(stages.ToString()));
        bridge.Close();
    }

    [Fact]
    public void TheCheckOfAKeptBridgeCountsTheRequestsItMade()
    {
        FakeDt295 fake = FakeDt295.Locking();
        (DevicePacing pacing, SteppedClock clock) = PixDt295Support.Pacing(fake);
        Dt295Bridge bridge = Dt295Bridge.Open(new FakeDt295Host(fake), Path, pacing, null, fake.HandlerHash());
        var stages = new StageTimes(clock);
        long before = fake.Requests;

        Assert.True(bridge.StillAsLeft(stages));

        Assert.Equal($"0.0 ms in all (kept bridge check 0.0 ms/{fake.Requests - before} requests/0.0 ms in 0 sleeps)", stages.ToString());
        bridge.Close();
    }

    [Fact]
    public void ASatelliteTuneCountsTheLockAndTheTransportStreamApart()
    {
        FakeDt295 fake = FakeDt295.Locking();
        (DevicePacing pacing, SteppedClock clock) = PixDt295Support.Pacing(fake);
        var frontend = new Dt295Frontend(PixDt295Support.Chips(fake), pacing);
        var stages = new StageTimes(clock);

        frontend.TuneSatellite(1_049_480, null, Path, stages);

        Assert.Equal(["tune registers", "lock"], StageNames(stages.ToString()).Take(2));
    }

    [GeneratedRegex(@"[(,] ?([A-Za-z][A-Za-z0-9 -]*?) \d+\.\d ms")]
    private static partial Regex StageName();

    [GeneratedRegex(@"/(\d+) requests")]
    private static partial Regex Requests();

    [GeneratedRegex(@"^Information /dev/bus/usb/001/005: started, tuned to terrestrial channel 22 and streaming after \d+ ms; locked True, C/N \d+\.\d dB\.$")]
    private static partial Regex Summary();

    private sealed class CardsThatAreNotInTheSlot : IDescramblerFactory
    {
        public bool Unscrambles => true;

        public CardIdentity? CardInHand => null;

        public IDescrambler? Open(HeadHold hold) => new ScriptedDescrambler(stream => stream.ToArray());
    }
}
