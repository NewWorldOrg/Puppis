using System.Text.RegularExpressions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed partial class DvbOpeningStagesTests
{
    private const FrontendStatus Locked =
        FrontendStatus.Signal
        | FrontendStatus.Carrier
        | FrontendStatus.Viterbi
        | FrontendStatus.Sync
        | FrontendStatus.Lock;

    private static readonly DeviceSettings Terrestrial = new("tuner-a", DeviceKind.Terrestrial, "/dev/dvb/adapter0/frontend0");

    private static readonly DeviceSettings Satellite = new(
        "tuner-b",
        DeviceKind.Satellite,
        "/dev/dvb/adapter1/frontend0",
        LnbPower: true
    );

    private static (TunerDeviceFactory Factory, ScriptedDvbSystemCalls Calls, CapturingLogger<TunerDeviceFactory> Log) Ready()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = new ScriptedDvbSystemCalls(clock);
        var log = new CapturingLogger<TunerDeviceFactory>();
        calls.ReportStatus(Locked);

        return (
            TunerDeviceFactory.Using(
                new DriverConfiguration(null, null, 0, new TunerSettings(TunerBackend.Dvb), null),
                clock,
                calls,
                logger: log
            ),
            calls,
            log
        );
    }

    private static string StagesLine(CapturingLogger<TunerDeviceFactory> log) =>
        Assert.Single(log.Lines, line => line.Contains("(DVB, ", StringComparison.Ordinal));

    [Fact]
    public void ALiveOpeningSaysOnceHowLongEachStageTookAndWhatItWasFor()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls, CapturingLogger<TunerDeviceFactory> log) = Ready();
        calls.ReportStatusesInTurn(FrontendStatus.Signal, FrontendStatus.Signal | FrontendStatus.Carrier);
        calls.Deliver(new byte[564]);
        calls.Deliver(new byte[376]);

        using ITunerDevice device = factory.Create(
            Terrestrial,
            new TuningRequest(TunerKind.Terrestrial, 27),
            null,
            HeadHold.WhatIsHeld,
            SessionPurpose.Live
        );

        string line = StagesLine(log);

        Assert.Matches(
            @"^Information devices\['tuner-a'\] \(DVB, terrestrial\) for live on physical channel 27: opened and locked in "
                + @"\d+\.\d ms in all \(frontend \d+\.\d ms, demux \d+\.\d ms, demux buffer \d+\.\d ms, "
                + @"filter \d+\.\d ms, dvr \d+\.\d ms, tune \d+\.\d ms, "
                + @"lock \d+\.\d ms/3 polls, discard \d+\.\d ms/940 bytes\)\.$",
            line
        );
    }

    [Fact]
    public void ARecordingOpensItsStreamAfterTheLockAndDiscardsNothing()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Ready();

        using ITunerDevice device = factory.Create(
            Terrestrial,
            new TuningRequest(TunerKind.Terrestrial, 22),
            null,
            HeadHold.EveryProgramme,
            SessionPurpose.Recording
        );

        string line = StagesLine(log);

        Assert.Matches(
            @"for recording on physical channel 22: opened and locked in \d+\.\d ms in all "
                + @"\(frontend \d+\.\d ms, tune \d+\.\d ms, lock \d+\.\d ms/1 polls, "
                + @"demux \d+\.\d ms, demux buffer \d+\.\d ms, filter \d+\.\d ms, dvr \d+\.\d ms\)\.$",
            line
        );
        Assert.DoesNotContain("discard", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASatelliteOpeningCountsSettingTheAerialSupplyAsAStageOfItsOwn()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Ready();
        var tune = new TuneParams { System = TuneSystem.IsdbSBs, IsdbSBs = new IsdbSBsParams(15, 16625) };

        using ITunerDevice device = factory.Create(
            Satellite,
            tune.ToLegacyRequest(),
            tune,
            HeadHold.EveryProgramme,
            SessionPurpose.Survey
        );

        string line = StagesLine(log);

        Assert.Contains(
            "devices['tuner-b'] (DVB, satellite) for survey on BS slot 15 transport stream 16625: opened and locked in ",
            line,
            StringComparison.Ordinal
        );
        Assert.Matches(@"frontend \d+\.\d ms, aerial supply \d+\.\d ms, tune ", line);
    }

    [Fact]
    public void AnOpeningThatNeverLocksSaysHowLongItWaitedAndHowOftenItAsked()
    {
        (TunerDeviceFactory factory, ScriptedDvbSystemCalls calls, CapturingLogger<TunerDeviceFactory> log) = Ready();
        calls.ReportStatus(FrontendStatus.Signal);

        Assert.Throws<DvbDeviceException>(() =>
            factory.Create(
                Terrestrial,
                new TuningRequest(TunerKind.Terrestrial, 27),
                null,
                HeadHold.EveryProgramme,
                SessionPurpose.Scan
            )
        );

        string line = StagesLine(log);
        Match polls = Polls().Match(line);

        Assert.Contains("for scan on physical channel 27: gave up without a lock after ", line, StringComparison.Ordinal);
        Assert.True(polls.Success);
        Assert.Equal(calls.StatusReads, int.Parse(polls.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnOpeningNobodyGaveAPurposeIsStillNamedByItsDeviceAndChannel()
    {
        (TunerDeviceFactory factory, _, CapturingLogger<TunerDeviceFactory> log) = Ready();

        using ITunerDevice device = factory.Create(
            Terrestrial,
            new TuningRequest(TunerKind.Terrestrial, 27),
            null,
            HeadHold.EveryProgramme
        );

        Assert.Contains(
            "devices['tuner-a'] (DVB, terrestrial) for an unstated purpose on physical channel 27: opened and locked in ",
            StagesLine(log),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ATunerOpenedWithoutAFactoryIsNamedByItsFrontendAndWritesNothingWhenNobodyListens()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = new ScriptedDvbSystemCalls(clock);
        var log = new CapturingLogger<DvbTunerDevice>();
        calls.ReportStatus(Locked);
        DvbDevicePaths paths = new("/dev/dvb/adapter0/frontend0", "/dev/dvb/adapter0/demux0", "/dev/dvb/adapter0/dvr0");

        DvbTunerDevice.Open(calls, clock, paths, DvbChannel.Terrestrial(55), LnbVoltage.Off, DvbTunerSettings.Default, CancellationToken.None).Dispose();
        DvbTunerDevice.Open(calls, clock, paths, DvbChannel.Terrestrial(55), LnbVoltage.Off, DvbTunerSettings.Default, CancellationToken.None, log).Dispose();

        Assert.StartsWith(
            "Information /dev/dvb/adapter0/frontend0 on physical channel 55: opened and locked in ",
            Assert.Single(log.Lines),
            StringComparison.Ordinal
        );
    }

    [GeneratedRegex(@"lock \d+\.\d ms/(\d+) polls")]
    private static partial Regex Polls();
}
