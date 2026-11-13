using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Ipc;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class TuneFailureClassificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly DriverConfiguration Configuration = new(
        "/run/carina/driver.sock",
        [],
        6,
        new TunerSettings(TunerBackend.Fake),
        [new DeviceSettings("adapter0", DeviceKind.Terrestrial)]
    );

    private static readonly DriverHello Hello = new(DriverProtocol.Version, "instance-1", []);

    private readonly ManualTimeProvider clock = new(Start);

    [Fact]
    public void AFrontendThatDidNotLockIsAnsweredAsThatChannelsOutcome()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() => DvbFailure.NoLock(
            "/dev/dvb/adapter0/frontend0: the frontend did not lock within 5 seconds,"
            + " and the last status it reported while waiting was None."
        )));

        SessionStart start = manager.Begin(Request("scan-14", 14));

        Assert.Equal(SessionRefusal.NoLock, start.Refusal);
        Assert.Contains("did not lock", start.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrontendThatLockedAndDeliveredNothingIsAnsweredAsThatChannelsOutcome()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() =>
            DvbFailure.LockedWithoutData(
                "/dev/dvb/adapter0/dvr0: the frontend is still locked and no transport stream"
                + " bytes arrived within 5 seconds."
            )));

        SessionStart start = manager.Begin(Request("scan-14", 14));

        Assert.Equal(SessionRefusal.NoData, start.Refusal);
        Assert.Contains("delivered nothing", start.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceThatCannotBeOpenedIsStillAnsweredAsUnavailable()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() => DvbFailure.AtDevice(
            "/dev/dvb/adapter0/frontend0",
            "opening the frontend",
            Errno.Busy,
            "Another process holds the frontend."
        )));

        SessionStart start = manager.Begin(Request("scan-14", 14));

        Assert.Equal(SessionRefusal.DeviceUnavailable, start.Refusal);
    }

    [Fact]
    public void AChannelThatDeliveredNoBytesEndsItsSessionWithoutCondemningTheTuner()
    {
        var devices = new ConcurrentQueue<ITunerDevice>(
            [new SilentAfterLockDevice(), new ScriptedTunerDevice()]
        );
        TunerSessionManager manager = Manager(new QueuedDeviceFactory(devices));

        SessionStart first = manager.Begin(Request("scan-14", 14));

        Assert.True(first.TryGetSession(out TunerSession? silent));

        silent.WaitForEnd(Deadlock);

        Assert.Equal(SessionState.Failed, silent.State);
        Assert.NotEqual(SessionStopReason.DeviceFailed, silent.StopReason);
        Assert.False(manager.IsFaulted("adapter0", out _));

        Assert.Equal(
            SessionRefusalTitles.NoData,
            SessionViews.Of(silent, Hello).FailureTitle
        );

        SessionStart second = manager.Begin(Request("scan-15", 15));

        Assert.Equal(SessionRefusal.None, second.Refusal);
        Assert.True(second.TryGetSession(out TunerSession? next));

        next.Stop();
        next.WaitForEnd(Deadlock);
    }

    [Fact]
    public void AChannelThatKeepsDeliveringNothingAfterLockingLeavesTheTunerServingOtherChannels()
    {
        TunerSessionManager manager = Manager(new SilentChannelDeviceFactory(silentChannels: [27]));

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            SessionStart start = manager.Begin(Request($"survey-{attempt}", 27, SessionPurpose.Survey));

            Assert.True(start.TryGetSession(out TunerSession? session));

            session.WaitForEnd(Deadlock);

            Assert.Equal(SessionState.Failed, session.State);
            Assert.Equal(SessionRefusalTitles.NoData, SessionViews.Of(session, Hello).FailureTitle);
            Assert.False(manager.IsFaulted("adapter0", out _));

            clock.Advance(TimeSpan.FromSeconds(6));
        }

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerState.Idle, tuner.State);
        Assert.Equal(TunerHealthLevel.Degraded, tuner.Health?.Level);
        Assert.Equal(TunerDegradedKind.TuneFailing, tuner.Health?.DegradedKind);

        SessionStart other = manager.Begin(Request("survey-other", 21, SessionPurpose.Survey));

        Assert.Equal(SessionRefusal.None, other.Refusal);
        Assert.True(other.TryGetSession(out TunerSession? served));

        served.Stop();
        served.WaitForEnd(Deadlock);

        Assert.Equal(SessionState.Stopped, served.State);
    }

    [Fact]
    public void AChannelThatKeepsFailingToLockIsRefusedEachTimeAndTheTunerStaysInService()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14]));

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            SessionStart start = manager.Begin(Request($"survey-{attempt}", 14, SessionPurpose.Survey));

            Assert.Equal(SessionRefusal.NoLock, start.Refusal);
            Assert.Contains("terrestrial channel 14", start.Detail, StringComparison.Ordinal);

            clock.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.False(manager.IsFaulted("adapter0", out _));

        SessionStart other = manager.Begin(Request("survey-other", 20, SessionPurpose.Survey));

        Assert.True(other.TryGetSession(out TunerSession? served));

        served.Stop();
        served.WaitForEnd(Deadlock);
    }

    [Fact]
    public void ARefusalForDeliveringNothingNamesTheChannel()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() =>
            DvbFailure.LockedWithoutData("the frontend is still locked and nothing arrived.")));

        SessionStart start = manager.Begin(Request("survey-1", 14, SessionPurpose.Survey));

        Assert.Equal(SessionRefusal.NoData, start.Refusal);
        Assert.Contains("terrestrial channel 14", start.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingOnTwoDifferentChannelsInARowFaultsTheTuner()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14, 15]));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("survey-1", 14, SessionPurpose.Survey)));
        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("survey-2", 14, SessionPurpose.Survey)));

        Assert.False(manager.IsFaulted("adapter0", out _));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("survey-3", 15, SessionPurpose.Survey)));

        Assert.True(manager.IsFaulted("adapter0", out string? detail));
        Assert.Contains("terrestrial channel 14", detail, StringComparison.Ordinal);
        Assert.Contains("terrestrial channel 15", detail, StringComparison.Ordinal);
        Assert.Equal(
            SessionRefusal.FaultedDevice,
            manager.Begin(Request("survey-4", 20, SessionPurpose.Survey)).Refusal
        );
    }

    [Fact]
    public void ASessionThatDeliveredBetweenTwoFailingChannelsKeepsTheTunerInService()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14, 15, 16]));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("before", 14, SessionPurpose.Survey)));

        Assert.True(manager.Begin(Request("delivering", 20, SessionPurpose.Survey)).TryGetSession(out TunerSession? session));

        session.Stop();
        session.WaitForEnd(Deadlock);

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("after", 15, SessionPurpose.Survey)));
        Assert.False(manager.IsFaulted("adapter0", out _));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("the-last-straw", 16, SessionPurpose.Survey)));
        Assert.True(manager.IsFaulted("adapter0", out _));
    }

    [Fact]
    public void TwoDifferentChannelsThatGoSilentAfterLockingInARowFaultTheTuner()
    {
        TunerSessionManager manager = Manager(new SilentChannelDeviceFactory(silentChannels: [14, 15]));

        foreach (int channel in new[] { 14, 15 })
        {
            Assert.True(
                manager.Begin(Request($"survey-{channel}", channel, SessionPurpose.Survey))
                    .TryGetSession(out TunerSession? session)
            );

            session.WaitForEnd(Deadlock);

            Assert.Equal(SessionState.Failed, session.State);
            Assert.NotEqual(SessionStopReason.DeviceFailed, session.StopReason);

            clock.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.True(manager.IsFaulted("adapter0", out string? detail));
        Assert.Contains("terrestrial channel 15", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyChannelsAScanMeetsNeitherCountTowardsAFaultNorExcuseTheOthers()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14, 15, 16]));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("survey-14", 14, SessionPurpose.Survey)));
        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("scan-15", 15)));

        Assert.False(manager.IsFaulted("adapter0", out _));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("survey-16", 16, SessionPurpose.Survey)));

        Assert.True(manager.IsFaulted("adapter0", out _));
    }

    [Fact]
    public void ASweepAcrossManyEmptyChannelsLeavesTheTunerInGoodStanding()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() => DvbFailure.NoLock(
            "the frontend did not lock within 5 seconds."
        )));

        for (int channel = 13; channel <= 22; channel++)
        {
            SessionStart start = manager.Begin(Request($"scan-{channel}", channel));

            Assert.Equal(SessionRefusal.NoLock, start.Refusal);

            clock.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.False(manager.IsFaulted("adapter0", out _));
    }

    [Fact]
    public void ATunerFaultedForNotLockingSaysSoInItsHealth()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14, 15]));

        Refusal(manager, Request("survey-14", 14, SessionPurpose.Survey));
        Refusal(manager, Request("survey-15", 15, SessionPurpose.Survey));

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerState.Faulted, tuner.State);
        Assert.Equal(SessionRefusalTitles.NoLock, tuner.Health?.FaultTitle);
        Assert.Equal(TunerFaultKind.RepeatedTuneFailure, tuner.Health?.FaultKind);
    }

    [Fact]
    public void ATunerFaultedForDeliveringNothingAfterLockingSaysSoInItsHealth()
    {
        TunerSessionManager manager = Manager(new SilentChannelDeviceFactory(silentChannels: [14, 15]));

        foreach (int channel in new[] { 14, 15 })
        {
            Assert.True(
                manager.Begin(Request($"survey-{channel}", channel, SessionPurpose.Survey))
                    .TryGetSession(out TunerSession? session)
            );

            session.WaitForEnd(Deadlock);

            clock.Advance(TimeSpan.FromSeconds(6));
        }

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerState.Faulted, tuner.State);
        Assert.Equal(SessionRefusalTitles.NoData, tuner.Health?.FaultTitle);
    }

    [Fact]
    public void ATunerFaultedForAnotherCauseNamesNoTuningFailure()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() => DvbFailure.NoLock(
            "the frontend did not lock within 5 seconds."
        )));

        manager.Begin(Request("scan-1", 14));
        manager.Fault(new TunerContradiction("adapter0", DeviceKind.Terrestrial, [DeviceKind.Satellite]));

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerState.Faulted, tuner.State);
        Assert.Null(tuner.Health?.FaultTitle);
    }

    [Fact]
    public void ATunerThatHasNotFaultedNamesNoTuningFailureEvenAfterFailingToLock()
    {
        TunerSessionManager manager = Manager(new ThrowingDeviceFactory(() => DvbFailure.NoLock(
            "the frontend did not lock within 5 seconds."
        )));

        manager.Begin(Request("scan-1", 14));

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerState.Idle, tuner.State);
        Assert.Null(tuner.Health?.FaultTitle);
        Assert.Equal(TunerHealthLevel.Degraded, tuner.Health?.Level);
        Assert.Equal(TunerDegradedKind.TuneFailing, tuner.Health?.DegradedKind);
        Assert.Equal(TunerFaultKind.Unspecified, tuner.Health?.FaultKind);
    }

    [Fact]
    public void ATunerThatDeliversASessionThroughAfterFailingToTuneIsHealthyAgain()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14]));

        Assert.Equal(SessionRefusal.NoLock, Refusal(manager, Request("scan-1", 14)));
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(
            TunerHealthLevel.Degraded,
            Assert.Single(SessionViews.Tuners(Configuration, manager)).Health?.Level);

        Assert.True(manager.Begin(Request("scan-2", 20)).TryGetSession(out TunerSession? delivered));

        delivered.Stop();
        delivered.WaitForEnd(Deadlock);

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerHealthLevel.Healthy, tuner.Health?.Level);
        Assert.Equal(TunerDegradedKind.Unspecified, tuner.Health?.DegradedKind);
    }

    [Fact]
    public void ATunerThatFaultedIsFaultedRatherThanDegraded()
    {
        TunerSessionManager manager = Manager(new FailingChannelDeviceFactory(deadChannels: [14, 15]));

        Refusal(manager, Request("survey-14", 14, SessionPurpose.Survey));
        Refusal(manager, Request("survey-15", 15, SessionPurpose.Survey));

        TunerSnapshot tuner = Assert.Single(SessionViews.Tuners(Configuration, manager));

        Assert.Equal(TunerHealthLevel.Faulted, tuner.Health?.Level);
        Assert.Equal(TunerDegradedKind.Unspecified, tuner.Health?.DegradedKind);
    }

    private sealed class SilentChannelDeviceFactory(int[] silentChannels) : ITunerDeviceFactory
    {
        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold) =>
            silentChannels.Contains(tuning.PhysicalChannel)
                ? new SilentAfterLockDevice()
                : new ScriptedTunerDevice();
    }

    private SessionRefusal Refusal(TunerSessionManager manager, StartSessionRequest request)
    {
        SessionRefusal refusal = manager.Begin(request).Refusal;

        clock.Advance(TimeSpan.FromSeconds(6));

        return refusal;
    }

    private TunerSessionManager Manager(ITunerDeviceFactory factory) =>
        new(
            Configuration,
            factory,
            clock,
            NullLogger<TunerSessionManager>.Instance
        );

    private static StartSessionRequest Request(
        string sessionId,
        int channel,
        SessionPurpose purpose = SessionPurpose.Scan
    ) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = purpose,
            Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
            EndsAt = Start.AddHours(1),
        };

    private sealed class ThrowingDeviceFactory(Func<Exception> failure) : ITunerDeviceFactory
    {
        public ITunerDevice Create(
            DeviceSettings device,
            TuningRequest tuning,
            TuneParams? tune,
            HeadHold hold
        ) => throw failure();
    }

    private sealed class QueuedDeviceFactory(ConcurrentQueue<ITunerDevice> devices)
        : ITunerDeviceFactory
    {
        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            Assert.True(devices.TryDequeue(out ITunerDevice? next), "The test scripted too few devices.");

            return next;
        }
    }

    private sealed class SilentAfterLockDevice : ITunerDevice
    {
        public long Overflows => 0;

        public bool Disposed { get; private set; }

        public byte[] Read(int count, CancellationToken cancellationToken) =>
            throw DvbFailure.LockedWithoutData(
                "/dev/dvb/adapter0/dvr0: no transport stream bytes arrived within 5 seconds,"
                + " and the frontend reports it is still locked."
            );

        public void Dispose() => Disposed = true;
    }
}
