using System.Globalization;
using System.Text.RegularExpressions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed partial class CechZd1TuneStagesTests
{
    private const string Device = "/dev/bus/usb/001/002";

    private static readonly DeviceSettings Tuner = new("usb1-1", DeviceKind.Terrestrial, Device);

    private static readonly string[] TunerAndAgc =
    [
        "wideband detector",
        "temperature",
        "PLL",
        "capacitor search",
        "tuner steps 0 and 1",
        "band, timing and PWM",
        "diversity inside only",
        "AGC start-up",
    ];

    private static readonly string[] SearchThatLocks =
    [
        "begin",
        "first channel configuration",
        "start",
        "wait for the search",
        "second channel configuration",
        "sync",
        "lock",
        "data",
    ];

    private static (TunerDeviceFactory Factory, FakeChip Demodulator, CapturingLogger<TunerDeviceFactory> Log) Factory()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        var host = new FakeUsbHost(UsbTestSupport.BridgeWith(demodulator, tuner));
        (DevicePacing pacing, _, _) = UsbTestSupport.Pacing();
        var log = new CapturingLogger<TunerDeviceFactory>();

        return (
            TunerDeviceFactory.Using(
                new DriverConfiguration(
                    OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                    Tuner: new TunerSettings(TunerBackend.Usb),
                    Devices: [Tuner]
                ),
                host,
                pacing,
                logger: log
            ),
            demodulator,
            log
        );
    }

    private static ITunerDevice Live(TunerDeviceFactory factory, int channel = 22) =>
        factory.Create(Tuner, new TuningRequest(TunerKind.Terrestrial, channel), null, HeadHold.WhatIsHeld, SessionPurpose.Live);

    private static IReadOnlyList<string> TuneLines(CapturingLogger<TunerDeviceFactory> log) =>
        [.. log.Lines.Where(line => line.Contains("(CECH-ZD1, ", StringComparison.Ordinal))];

    private static IReadOnlyList<string> StageNames(string line)
    {
        string listed = line[line.IndexOf('(', line.IndexOf(" ms in all", StringComparison.Ordinal))..];

        return [.. StageName().Matches(listed).Select(match => match.Groups[1].Value)];
    }

    private static long Sum(Regex counted, string line) =>
        counted.Matches(line).Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));

    private static string[] Search(string which, IEnumerable<string> parts) => [.. parts.Select(part => $"{which} search {part}")];

    [Fact]
    public void AStartedTunerSaysOnceWhereTheTimeOfItsTuneWent()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();

        using (Live(factory))
        {
            string line = Assert.Single(TuneLines(log));

            Assert.StartsWith(
                "Information devices['usb1-1'] (CECH-ZD1, terrestrial) for live on physical channel 22: the tune of the started tuner took ",
                line,
                StringComparison.Ordinal
            );
            Assert.Equal(
                ["diversity both and output off", .. TunerAndAgc, .. Search("first", SearchThatLocks), "output on and LED"],
                StageNames(line)
            );
            Assert.DoesNotContain("failed", line, StringComparison.Ordinal);
        }

        factory.Dispose();
    }

    [Fact]
    public void AWokenTunerCountsWakingAndTheDcOffsetCalibrationAsStagesOfTheirOwn()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();
        Live(factory).Dispose();

        using (Live(factory, 27))
        {
            IReadOnlyList<string> lines = TuneLines(log);

            Assert.Equal(2, lines.Count);
            Assert.Contains("for live on physical channel 27: the tune of the woken tuner took ", lines[1], StringComparison.Ordinal);
            Assert.Equal(
                ["wake", "diversity both and output off", "DC offset calibration", .. TunerAndAgc, .. Search("first", SearchThatLocks), "output on and LED"],
                StageNames(lines[1])
            );
            Assert.Matches(@"DC offset calibration \d+\.\d ms/[1-9]\d* requests/[1-9]\d*\.0 ms in [1-9]\d* sleeps", lines[1]);
        }

        factory.Dispose();
    }

    [Fact]
    public void TheStagesAddUpToTheRequestsTheSummaryLineGivesTheTune()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();

        using (Live(factory))
        {
            string summary = Assert.Single(log.Lines, line => line.Contains(": started and tuned in ", StringComparison.Ordinal));
            Match tune = TuneInTheSummary().Match(summary);

            Assert.True(tune.Success);
            Assert.Equal(
                long.Parse(tune.Groups[1].Value, CultureInfo.InvariantCulture),
                Sum(Requests(), Assert.Single(TuneLines(log)))
            );
        }

        factory.Dispose();
    }

    [Fact]
    public void TheSummaryLineKeepsItsWords()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Factory();

        using (Live(factory))
        {
            Assert.Single(log.Lines, line => Summary().IsMatch(line));
        }

        factory.Dispose();
    }

    [Fact]
    public void AFirstSearchThatFailsIsNamedWithWhereItStoppedAndWhatTheDemodulatorSaid()
    {
        (TunerDeviceFactory factory, FakeChip demodulator, CapturingLogger<TunerDeviceFactory> log) = Factory();
        demodulator.Answer(1284, 0x0001, 0x0001, 0x0001, 0x0002);

        using (Live(factory))
        {
            string line = Assert.Single(TuneLines(log));

            Assert.EndsWith(
                $"; the first search failed while waiting for the search, D[1284] 0x0001, D[{Dib8000.Status}] not read.",
                line,
                StringComparison.Ordinal
            );
            Assert.Equal(
                [
                    "diversity both and output off",
                    .. TunerAndAgc,
                    .. Search("first", ["begin", "first channel configuration", "start", "wait for the search"]),
                    .. Search("second", ["begin", "start", "wait for the search", "first channel configuration", "second channel configuration", "sync", "lock", "data"]),
                    "output on and LED",
                ],
                StageNames(line)
            );
        }

        factory.Dispose();
    }

    [Fact]
    public void ATuneThatNeverLocksSaysWhereBothSearchesStopped()
    {
        (TunerDeviceFactory factory, FakeChip demodulator, CapturingLogger<TunerDeviceFactory> log) = Factory();
        demodulator.Answer(1284, 0x0001, 0x0001, 0x0001, 0x0002);
        demodulator.Answer(Dib8000.Status, 0x0000);

        Assert.Throws<DvbDeviceException>(() => Live(factory));

        string line = Assert.Single(TuneLines(log));

        Assert.Contains("for live on physical channel 22: the tune that did not lock took ", line, StringComparison.Ordinal);
        Assert.Contains("; the first search failed while waiting for the search, D[1284] 0x0001, ", line, StringComparison.Ordinal);
        Assert.EndsWith(
            $"; the second search failed while waiting for sync, D[1284] 0x0002, D[{Dib8000.Status}] 0x0000.",
            line,
            StringComparison.Ordinal
        );
        factory.Dispose();
    }

    [Fact]
    public void ATuneGivenUpOnBecauseTheDemodulatorNeverAnsweredStillSaysWhereItsTimeWent()
    {
        (TunerDeviceFactory factory, FakeChip demodulator, CapturingLogger<TunerDeviceFactory> log) = Factory();
        demodulator.Answer(1284, 0);

        DvbDeviceException refused = Assert.Throws<DvbDeviceException>(() => Live(factory));

        string line = Assert.Single(TuneLines(log));

        Assert.NotEqual(TuningFailure.NoLock, refused.Failure);
        Assert.Contains("for live on physical channel 22: the tune that was given up took ", line, StringComparison.Ordinal);
        Assert.Contains("first search wait for the search ", line, StringComparison.Ordinal);
        factory.Dispose();
    }

    [Fact]
    public void TheSleepsOfTheStagesAreTheOnesTheTuneSlept()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        (DevicePacing pacing, ManualTimeProvider clock, List<TimeSpan> slept) = UsbTestSupport.Pacing();
        var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
        frontend.Initialise();
        slept.Clear();
        var stages = new StageTimes(clock);
        stages.Count(() => 0, frontend.Pacing);

        Assert.NotEqual(SearchOutcome.Failed, frontend.Tune(BroadcastStandards.TerrestrialCentreHz(22), stages));

        Assert.NotEmpty(slept);
        Assert.Equal(slept.Count, Sum(Sleeps(), stages.ToString()));
        Assert.Equal(
            slept.Sum(span => span.TotalMilliseconds),
            SleptFor().Matches(stages.ToString()).Sum(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
        );
    }

    [Fact]
    public void ATuneNobodyTimesSleepsAndWritesExactlyAsATimedOneDoes()
    {
        (IReadOnlyList<(int, ushort)> Written, IReadOnlyList<TimeSpan> Slept) Tuned(bool timed)
        {
            (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
            (DevicePacing pacing, ManualTimeProvider clock, List<TimeSpan> slept) = UsbTestSupport.Pacing();
            var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
            frontend.Initialise();
            frontend.Sleep();
            StageTimes? stages = timed ? new StageTimes(clock) : null;
            stages?.Count(() => 0, frontend.Pacing);
            frontend.Tune(BroadcastStandards.TerrestrialCentreHz(27), stages);

            return ([.. demodulator.Written, .. tuner.Written], slept);
        }

        (IReadOnlyList<(int, ushort)> written, IReadOnlyList<TimeSpan> slept) = Tuned(timed: false);
        (IReadOnlyList<(int, ushort)> writtenTimed, IReadOnlyList<TimeSpan> sleptTimed) = Tuned(timed: true);

        Assert.Equal(written, writtenTimed);
        Assert.Equal(slept, sleptTimed);
    }

    [GeneratedRegex(@"[(,] ?([A-Za-z][A-Za-z0-9, -]*?) \d+\.\d ms/")]
    private static partial Regex StageName();

    [GeneratedRegex(@"/(\d+) requests")]
    private static partial Regex Requests();

    [GeneratedRegex(@" ms in (\d+) sleeps")]
    private static partial Regex Sleeps();

    [GeneratedRegex(@"requests/(\d+\.\d) ms in ")]
    private static partial Regex SleptFor();

    [GeneratedRegex(@"tune \d+ ms/(\d+) requests")]
    private static partial Regex TuneInTheSummary();

    [GeneratedRegex(@"^Information /dev/bus/usb/001/002: started and tuned in \d+ ms in all \(bridge \d+ ms/\d+ requests, frontend \d+ ms/\d+ requests, capture \d+ ms/\d+ requests, tune \d+ ms/\d+ requests, start \d+ ms/\d+ requests\)\.$")]
    private static partial Regex Summary();
}
