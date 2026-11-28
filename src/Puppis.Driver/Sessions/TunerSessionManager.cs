using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Diagnostics;
using Puppis.Driver.Events;
using Puppis.Driver.Reception;
using Puppis.Driver.Recording;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Sessions;

public sealed class TunerSessionManager(
    DriverConfiguration configuration,
    ITunerDeviceFactory deviceFactory,
    TimeProvider timeProvider,
    ILogger<TunerSessionManager> logger,
    TimeSpan? hardStopLimit = null,
    DriverEventHub? events = null,
    DiagnosticsStore? diagnostics = null,
    IRecordingWriterFactory? recordingWriters = null,
    TimeSpan? tunerGrace = null,
    TimeSpan? letGoLimit = null,
    TimeSpan? handOverLimit = null,
    TimeSpan? progressInterval = null,
    ITunerDeviceCheck? deviceCheck = null,
    Func<DeviceSettings, bool?>? presence = null,
    TimeSpan? openLimit = null,
    ReceptionBook? reception = null,
    TimeSpan? measurementStopLimit = null,
    ServiceTableMemory? serviceTables = null
) : IHostedService
{
    public const int RetainedSessions = 64;

    public const int DifferentChannelsThatFaultTheDevice = 2;

    public const int LockReadingsThatCountAsNow = 2;

    public static readonly TimeSpan DefaultHardStopLimit = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LetGoLimit =
        DvbTunerSettings.Default.BytePatience + TimeSpan.FromSeconds(3);

    private readonly ServiceTableMemory tables = serviceTables ?? new ServiceTableMemory();

    private readonly ConcurrentDictionary<SessionId, TunerSession> sessions = [];
    private readonly ConcurrentDictionary<SessionId, TaskCompletionSource> starting = [];
    private readonly TunerPool pool = new(
        timeProvider,
        tunerGrace,
        [.. (configuration.Devices ?? []).Where(device => device?.Id is not null).Select(device => (device.Id!, device.Enabled))],
        deviceId => LetGo(configuration, deviceCheck, deviceId),
        logger,
        deviceId => Look(configuration, presence, deviceId),
        StandingIn(reception),
        deviceId => IsKeptOpen(configuration, deviceCheck, deviceId)
    );
    private readonly ConcurrentDictionary<string, DateTimeOffset> healthChangedAt = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentQueue<TunerSession> ended = new();
    private readonly ConcurrentDictionary<SessionId, TuningKey> tunings = [];
    private readonly ConcurrentDictionary<string, SessionId> writing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TuningKey>> tuneFailures = new(StringComparer.Ordinal);
    private readonly Lock streakGate = new();
    private readonly TimeSpan drainCap = TimeSpan.FromHours(
        Math.Max(0, configuration.ShutdownGraceHours)
    );
    private readonly TimeSpan hardStop = hardStopLimit ?? DefaultHardStopLimit;
    private readonly TimeSpan letGo = letGoLimit ?? LetGoLimit;
    private readonly TimeSpan handOver = handOverLimit ?? HandOverLimit;
    private readonly TimeSpan opening = openLimit ?? OpenLimit;
    private readonly TimeSpan measurementStop = measurementStopLimit ?? openLimit ?? OpenLimit;
    private readonly IRecordingWriterFactory writerFactory =
        recordingWriters ?? new RecordingWriterFactory();
    private FaultedTunerRecheck? rechecking;

    private FaultedTunerRecheck? recheck =>
        deviceCheck is null
            ? null
            : LazyInitializer.EnsureInitialized(
                ref rechecking,
                () => new FaultedTunerRecheck(deviceCheck, timeProvider, logger, pool.IsQuiet)
            );

    private readonly Lock drainGate = new();

    private volatile bool draining;
    private Task? drain;
    private RecordingProgressNotifier? progress;

    public IReadOnlyCollection<TunerSession> Sessions => [.. sessions.Values];

    public IReadOnlyCollection<TunerSession> Recent => [.. ended];

    public bool IsDraining => draining;

    public TimeSpan ShutdownBudget => drainCap + hardStop;

    public TimeSpan HardStopBudget => hardStop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        progress = new RecordingProgressNotifier(
            AnythingIsBeingRecorded,
            () => events?.Signal(DriverEvents.RecordingProgress),
            timeProvider,
            error => logger.LogWarning(
                error,
                "The driver could not announce how a recording is going; the next round will try again."
            ),
            progressInterval
        );

        return Task.CompletedTask;
    }

    private bool AnythingIsBeingRecorded() =>
        sessions.Values.Any(session => session.Purpose is SessionPurpose.Recording);

    public void EnterDraining()
    {
        lock (drainGate)
        {
            EnterDrainingUnderGate();
        }
    }

    public bool TryEnterDrainingUnlessRecording(out IReadOnlyList<TunerSession> recordings)
    {
        lock (drainGate)
        {
            TunerSession[] held = sessions
                .Values.Where(session => session.Purpose is SessionPurpose.Recording)
                .ToArray();

            if (held.Length > 0)
            {
                recordings = held;

                return false;
            }

            EnterDrainingUnderGate();
            recordings = [];

            return true;
        }
    }

    private void EnterDrainingUnderGate()
    {
        if (draining)
        {
            return;
        }

        draining = true;
        events?.Signal(DriverEvents.Draining);
    }

    public void DetachEverySubscriber()
    {
        foreach (TunerSession session in sessions.Values)
        {
            session.Broadcaster.Close(
                new OperationCanceledException(
                    $"The driver is shutting down; the stream of '{session.SessionId}' ends here and is incomplete."
                ),
                SessionStopReason.Requested
            );
        }
    }

    public Task DrainAsync(CancellationToken cancellationToken)
    {
        lock (drainGate)
        {
            drain ??= DrainOnceAsync(cancellationToken);

            return drain;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync(cancellationToken);

    private async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainEverySessionAsync(cancellationToken);
        }
        finally
        {
            progress?.Dispose();
            recheck?.Dispose();
        }
    }

    private async Task DrainEverySessionAsync(CancellationToken cancellationToken)
    {
        TunerSession[] running;

        lock (drainGate)
        {
            EnterDrainingUnderGate();
            running = [.. sessions.Values];
        }

        if (running.Length is 0)
        {
            pool.Dispose();

            return;
        }

        TunerSession[] recordings = running
            .Where(session => session.Purpose is SessionPurpose.Recording)
            .ToArray();

        foreach (TunerSession? session in running.Except(recordings))
        {
            session.Stop();
        }

        Task everyone = Task.WhenAll(running.Select(session => session.Completion));

        if (recordings.Length > 0)
        {
            logger.LogInformation(
                "Shutdown was asked for while {Count} recordings were running; staying up for up to {DrainCap}.",
                recordings.Length,
                drainCap
            );

            Task theRecordings = Task.WhenAll(recordings.Select(session => session.Completion));

            if (await SettlesAsync(theRecordings, drainCap, cancellationToken))
            {
                if (!await SettlesAsync(everyone, hardStop, CancellationToken.None))
                {
                    GiveUpOn(running);
                }

                pool.Dispose();

                return;
            }

            foreach (TunerSession? session in recordings.Where(session => !session.Completion.IsCompleted))
            {
                logger.LogWarning(
                    "Recording {SessionId} on {DeviceId} is being cut short after {BytesRecorded} bytes because shutdown could not wait any longer.",
                    session.SessionId.Value,
                    session.DeviceId,
                    session.BytesRecorded
                );

                session.Stop(SessionStopReason.DrainCapReached);
            }
        }

        if (!await SettlesAsync(everyone, hardStop, CancellationToken.None))
        {
            GiveUpOn(running);
        }

        pool.Dispose();
    }

    private void GiveUpOn(TunerSession[] running)
    {
        foreach (TunerSession? session in running.Where(session => !session.Completion.IsCompleted))
        {
            logger.LogError(
                "Session {SessionId} on {DeviceId} did not let go within {HardStopLimit}; the driver is exiting without it.",
                session.SessionId.Value,
                session.DeviceId,
                hardStop
            );
        }
    }

    private async Task<bool> SettlesAsync(
        Task everyone,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await everyone.WaitAsync(limit, timeProvider, cancellationToken);

            return true;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<SessionStart> BeginAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        IReadOnlyList<string> problems = request.Validate(now);
        if (problems.Count > 0)
        {
            return SessionStart.Refused(SessionRefusal.Rejected, string.Join(" ", problems));
        }

        if (draining)
        {
            return SessionStart.Refused(
                SessionRefusal.Draining,
                "The driver is shutting down, so no session can start."
            );
        }

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!starting.TryAdd(request.SessionId, started))
        {
            return SessionStart.Refused(
                SessionRefusal.DuplicateSession,
                $"The session '{request.SessionId}' is already starting."
            );
        }

        try
        {
            return await BeginAloneAsync(request, now, cancellationToken);
        }
        finally
        {
            starting.TryRemove(
                new KeyValuePair<SessionId, TaskCompletionSource>(request.SessionId, started)
            );
            started.TrySetResult();
        }
    }

    private async Task<SessionStart> BeginAloneAsync(
        StartSessionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (sessions.ContainsKey(request.SessionId))
        {
            return SessionStart.Refused(
                SessionRefusal.DuplicateSession,
                $"The session '{request.SessionId}' already exists."
            );
        }

        if (WhatEndedUnder(request.SessionId) is { } before && !CarriesOn(before, request))
        {
            return SessionStart.Refused(
                SessionRefusal.DuplicateSession,
                $"The session '{request.SessionId}' has already ended, and this is not the recording it was writing."
            );
        }

        if (!TryResolveOutput(request, out string? directory, out SessionStart? outputRefusal))
        {
            return outputRefusal;
        }

        if (!TryDeclaredDevices(request, out PoolRequest? asking, out SessionStart? deviceRefusal))
        {
            return deviceRefusal;
        }

        PoolGrant grant = pool.Acquire(asking);

        if (!grant.IsGranted)
        {
            return Refusal(request, grant);
        }

        var claim = new TunerClaim(pool, grant.DeviceId, request.SessionId);
        SessionStart start = await ServeAsync(request, grant, claim, directory, now, cancellationToken);

        if (claim.GaveUpOn is not { } stuck)
        {
            return start;
        }

        PoolGrant another = request.DeviceId is null
            ? pool.Acquire(asking with { LeftOut = [claim.DeviceId] })
            : PoolGrant.Refused(PoolVerdict.DeviceBusy, start.Detail);

        if (!another.IsGranted)
        {
            SayWhatHeldTheRequestUp(stuck, claim.DeviceId, request.SessionId);

            return Refusal(request, another);
        }

        logger.LogInformation(
            "Session {SessionId} gave up waiting for the device {DeviceId} and goes to {Another} instead.",
            request.SessionId.Value,
            claim.DeviceId,
            another.DeviceId
        );

        return await ServeAsync(
            request,
            another,
            new TunerClaim(pool, another.DeviceId, request.SessionId),
            directory,
            now,
            cancellationToken
        );
    }

    private async Task<SessionStart> ServeAsync(
        StartSessionRequest request,
        PoolGrant grant,
        TunerClaim claim,
        string? directory,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        try
        {
            DateTimeOffset endsAt = EndOf(request, now);

            return grant.Verdict switch
            {
                PoolVerdict.Shared => await RideAlongAsync(request, grant, claim, directory, now, endsAt, cancellationToken),
                PoolVerdict.Swapped => await TakeTheSeatAsync(request, grant, claim, directory, now, endsAt, cancellationToken),
                _ => await TakeTheTunerAsync(request, grant, claim, directory, now, endsAt, cancellationToken),
            };
        }
        catch
        {
            claim.Abandon();

            throw;
        }
    }

    private sealed class TunerClaim(TunerPool pool, string deviceId, SessionId sessionId)
    {
        private int reported;

        public void Leave()
        {
            if (Report())
            {
                pool.Leave(sessionId);
            }
        }

        public string DeviceId => deviceId;

        public TunerClearance? GaveUpOn { get; private set; }

        public void GiveUpOn(TunerClearance clearance, TunerRefusal why)
        {
            GiveBack(why);

            if (clearance.Awaits is not ClearanceKind.AHandOver)
            {
                GaveUpOn = clearance;
            }
        }

        public void GiveBack(TunerRefusal why)
        {
            if (Report())
            {
                pool.GiveBack(deviceId, sessionId, why);
            }
        }

        public void Abandon()
        {
            if (Report())
            {
                pool.Abandon(deviceId, sessionId);
            }
        }

        public void PassTo(TunerSession session)
        {
            if (!Report())
            {
                throw new InvalidOperationException(
                    $"The tuner '{deviceId}' was already given back for '{session.SessionId}', so the session cannot hold it."
                );
            }
        }

        private bool Report() => Interlocked.Exchange(ref reported, 1) is 0;
    }

    private SessionStart Refusal(StartSessionRequest request, PoolGrant grant) =>
        grant.Verdict switch
        {
            PoolVerdict.DeviceBusy => SessionStart.Refused(SessionRefusal.DeviceBusy, grant.Detail),
            PoolVerdict.DisabledDevice => SessionStart.Refused(SessionRefusal.DisabledDevice, grant.Detail),
            PoolVerdict.FaultedDevice when request.DeviceId is { } named => SessionStart.Refused(
                SessionRefusal.FaultedDevice,
                recheck?.NextTryOf(named) is { } nextTryAt
                    ? $"The device '{named}' is faulted and is not handed out until it opens again; it is next tried at {nextTryAt:O}: {grant.Detail}"
                    : $"The device '{named}' is faulted and is not handed out until the driver restarts: {grant.Detail}"
            ),
            PoolVerdict.FaultedDevice => SessionStart.Refused(SessionRefusal.FaultedDevice, grant.Detail),
            PoolVerdict.WrongDeviceKind => SessionStart.Refused(
                SessionRefusal.WrongDeviceKind,
                $"The device '{request.DeviceId}' serves {DeviceNamed(request.DeviceId!)?.Kind}, and the request asks for {KindOf(request)}."
            ),
            PoolVerdict.NoDeviceOfThatKind => SessionStart.Refused(SessionRefusal.NoDeviceOfThatKind, grant.Detail),
            PoolVerdict.DeviceUnavailable => SessionStart.Refused(SessionRefusal.DeviceUnavailable, grant.Detail),
            _ => SessionStart.Refused(SessionRefusal.NoDeviceFree, grant.Detail),
        };

    private DateTimeOffset EndOf(StartSessionRequest request, DateTimeOffset now)
    {
        if (!SessionPurposes.ReadsEveryPacket(request.Purpose))
        {
            return request.EndsAt ?? now.AddMinutes(configuration.LiveSessionMinutes);
        }

        DateTimeOffset latest = now.AddMinutes(configuration.WalkSessionMinutes);

        if (request.EndsAt is { } asked && asked < latest)
        {
            return asked;
        }

        if (request.EndsAt is { } cut)
        {
            logger.LogInformation(
                "Session {SessionId} asked to run until {Asked} and was cut to {Granted}.",
                request.SessionId.Value,
                cut,
                latest
            );
        }

        return latest;
    }

    private bool TryResolveOutput(
        StartSessionRequest request,
        out string? directory,
        [NotNullWhen(false)] out SessionStart? refusal
    )
    {
        directory = null;
        refusal = null;

        if (request.Purpose is not SessionPurpose.Recording)
        {
            return true;
        }

        if (configuration.TryResolveOutputRoot(request.OutputRoot, out string? resolved))
        {
            directory = resolved;

            return true;
        }

        string declared = string.Join(
            ", ",
            (configuration.OutputRoots ?? []).Select(root => root.Name)
        );

        refusal = SessionStart.Refused(
            SessionRefusal.UnknownOutputRoot,
            $"This driver declares no output root called '{request.OutputRoot}'; it declares {declared}."
        );

        return false;
    }

    private bool TryDeclaredDevices(
        StartSessionRequest request,
        [NotNullWhen(true)] out PoolRequest? asking,
        [NotNullWhen(false)] out SessionStart? refusal
    )
    {
        asking = null;
        refusal = null;

        IReadOnlyList<DeviceSettings> declared = configuration.Devices ?? [];
        TunerKind kind = KindOf(request);
        PoolRequest wanted = new(request.SessionId, request.Purpose, TuningKey.Of(request), request.DeviceId, [])
        {
            Serves = kind,
        };

        if (request.DeviceId is not { } named)
        {
            asking = wanted with
            {
                Candidates = [.. declared.Where(entry => Matches(entry.Kind, kind)).Select(entry => entry.Id!)],
            };

            return true;
        }

        if (declared.FirstOrDefault(entry => entry.Id == named) is not { } candidate)
        {
            refusal = SessionStart.Refused(
                SessionRefusal.UnknownDevice,
                $"No device is called '{named}'."
            );

            return false;
        }

        asking = wanted with
        {
            Candidates = [named],
            NamedServesTheKind = Matches(candidate.Kind, kind),
        };

        return true;
    }

    private async Task<SessionStart> TakeTheTunerAsync(
        StartSessionRequest request,
        PoolGrant grant,
        TunerClaim claim,
        string? directory,
        DateTimeOffset now,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken
    )
    {
        string deviceId = grant.DeviceId;
        SessionId sessionId = request.SessionId;

        foreach (SessionId displaced in grant.Displaced)
        {
            if (!sessions.TryGetValue(displaced, out TunerSession? loser))
            {
                continue;
            }

            logger.LogWarning(
                "Session {SessionId} on {DeviceId} is being cut off: {Detail}",
                loser.SessionId.Value,
                loser.DeviceId,
                grant.Detail
            );

            loser.Preempt(grant.Detail);
        }

        return await LeadAsync(request, claim, directory, now, endsAt, cancellationToken);
    }

    private async Task<SessionStart> LeadAsync(
        StartSessionRequest request,
        TunerClaim claim,
        string? directory,
        DateTimeOffset now,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken
    )
    {
        string deviceId = claim.DeviceId;
        SessionId sessionId = request.SessionId;

        if (pool.Clearance(deviceId, sessionId) is not { } clearance)
        {
            return Lost(claim, sessionId);
        }

        TimeSpan limit = LimitOf(clearance);
        TimeSpan left = limit - (timeProvider.GetUtcNow() - clearance.Since);
        long waitingSince = timeProvider.GetTimestamp();
        bool cleared;

        try
        {
            cleared = await clearance.Cleared.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            var why = new TunerRefusal(
                SessionRefusal.DeviceBusy,
                clearance.Awaits is ClearanceKind.AMeasurement
                    ? $"The device '{deviceId}' was asked for '{sessionId}', and {TunerPool.MeasurementUnderWay} on it did not stop within {limit}."
                    : $"The device '{deviceId}' was asked for '{sessionId}', and what was on it did not let go within {limit}."
            );

            claim.GiveUpOn(clearance, why);

            return SessionStart.Refused(why.Refusal, why.Detail);
        }

        SayAMeasurementWasStopped(clearance, deviceId, sessionId, timeProvider.GetElapsedTime(waitingSince));

        if (!cleared || pool.StartOpening(deviceId, sessionId) is not { } ticket)
        {
            return Lost(claim, sessionId);
        }

        if (ticket.Starts)
        {
            OpenInTheBackground(ticket, request);
        }

        TunerRefusal? failed;

        try
        {
            failed = ticket.Starts
                ? await ticket.Outcome.WaitAsync(cancellationToken)
                : await ticket.Outcome.WaitAsync(OpeningLeft(ticket), timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            failed = new TunerRefusal(
                SessionRefusal.DeviceUnavailable,
                $"The device '{deviceId}' did not open within {opening}."
            );
            pool.OpenTookTooLong(ticket, failed);
        }

        if (failed is not null)
        {
            claim.Leave();

            return SessionStart.Refused(failed.Refusal, failed.Detail);
        }

        if (pool.DeviceOf(deviceId) is not { } held)
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The device '{deviceId}' was let go before '{sessionId}' could take it over."
            );
        }

        return await OpenAsync(
            request,
            claim,
            deviceId,
            new LeasedTunerDevice(held),
            directory,
            now,
            endsAt,
            holds: true,
            tuned: ticket.Tunes
        );
    }

    private TimeSpan LimitOf(TunerClearance clearance) =>
        clearance.Awaits is ClearanceKind.AMeasurement ? measurementStop : handOver;

    private TimeSpan PatienceOfARiderOn(string deviceId) =>
        pool.IsStoppingAMeasurement(deviceId) ? handOver + measurementStop : handOver;

    private void SayAMeasurementWasStopped(TunerClearance clearance, string deviceId, SessionId sessionId, TimeSpan waited)
    {
        if (clearance.Awaits is ClearanceKind.AMeasurement)
        {
            logger.LogInformation(
                "The measurement of {DeviceId} on {Channel} was stopped for session {SessionId}, which then waited {Milliseconds:0} ms for the device.",
                deviceId,
                clearance.Measured,
                sessionId.Value,
                waited.TotalMilliseconds
            );
        }
    }

    private void SayWhatHeldTheRequestUp(TunerClearance stuck, string deviceId, SessionId sessionId)
    {
        if (stuck.Awaits is ClearanceKind.AMeasurement)
        {
            logger.LogError(
                "Session {SessionId} was refused because {MeasurementUnderWay} on the device {DeviceId} had not stopped after {Limit}, and no other tuner could be had.",
                sessionId.Value,
                TunerPool.MeasurementUnderWay,
                deviceId,
                measurementStop
            );
        }
    }

    private TimeSpan OpeningLeft(OpenTicket ticket)
    {
        TimeSpan left = opening - (timeProvider.GetUtcNow() - ticket.Since);

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private SessionStart Lost(TunerClaim claim, SessionId sessionId)
    {
        TunerRefusal? told = pool.RefusalOf(sessionId);

        claim.Leave();

        return told is { } refusal
            ? SessionStart.Refused(refusal.Refusal, refusal.Detail)
            : SessionStart.Refused(
                SessionRefusal.DeviceBusy,
                $"The device '{claim.DeviceId}' was taken from '{sessionId}' while it was being tuned."
            );
    }

    private void OpenInTheBackground(OpenTicket ticket, StartSessionRequest request) =>
        _ = Task.Factory.StartNew(
            () => OpenFor(ticket, request),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

    private void OpenFor(OpenTicket ticket, StartSessionRequest request)
    {
        string deviceId = ticket.DeviceId;
        ITunerDevice opened;

        try
        {
            DeviceSettings settings = DeviceNamed(deviceId)
                ?? throw new InvalidOperationException($"No device is called '{deviceId}'.");

            opened = deviceFactory.Create(
                settings,
                request.Tuning,
                request.Tune,
                HeadHolds.For(request.Purpose, request.Tuning, tables),
                request.Purpose
            );
        }
        catch (Exception error)
        {
            Report(() => pool.OpenFailed(
                ticket,
                RefusalForTheOpen(deviceId, request, error),
                error,
                () => NoteTheFailedOpen(deviceId, request, error)
            ), deviceId);

            return;
        }

        Report(() => pool.Opened(ticket, opened), deviceId);
    }

    private void Report(Action toTheTable, string deviceId)
    {
        try
        {
            toTheTable();
        }
        catch (Exception failed)
        {
            logger.LogError(failed, "What happened to the open of {DeviceId} was written down, and a step after it failed.", deviceId);
        }
    }

    private void NoteTheFailedOpen(string deviceId, StartSessionRequest request, Exception error)
    {
        if (error is not DvbDeviceException { Failure: TuningFailure.NoLock or TuningFailure.LockedWithoutData } failed)
        {
            return;
        }

        try
        {
            NoteTheReception(deviceId, TuningKey.Of(request), locked: false, null);
            RecordTuneFailure(deviceId, TuningKey.Of(request), failed, request.Purpose);
        }
        catch (Exception unexpected)
        {
            logger.LogError(unexpected, "The failed tune of {DeviceId} could not be written down.", deviceId);
        }
    }

    private static TunerRefusal RefusalForTheOpen(string deviceId, StartSessionRequest request, Exception error) =>
        error switch
        {
            DvbDeviceException { Failure: TuningFailure.NoLock } => new TunerRefusal(
                SessionRefusal.NoLock,
                $"The device '{deviceId}' opened but the frontend did not lock on {TuningKey.Of(request)}: {error.Message}"
            ),
            DvbDeviceException { Failure: TuningFailure.LockedWithoutData } => new TunerRefusal(
                SessionRefusal.NoData,
                $"The device '{deviceId}' locked on {TuningKey.Of(request)} but delivered nothing: {error.Message}"
            ),
            _ => new TunerRefusal(
                SessionRefusal.DeviceUnavailable,
                $"The device '{deviceId}' could not be opened: {error.Message}"
            ),
        };

    private async Task<SessionStart> RideAlongAsync(
        StartSessionRequest request,
        PoolGrant grant,
        TunerClaim claim,
        string? directory,
        DateTimeOffset now,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken
    )
    {
        TunerReadiness readiness = await pool.AwaitReadyAsync(
            grant.DeviceId,
            PatienceOfARiderOn(grant.DeviceId),
            request.SessionId,
            cancellationToken
        );

        if (readiness.Leads)
        {
            return await LeadAsync(request, claim, directory, now, endsAt, cancellationToken);
        }

        if (readiness is { Ready: false, Refusal: { } undone })
        {
            claim.Leave();

            return SessionStart.Refused(undone.Refusal, undone.Detail);
        }

        if (!readiness.Ready || !sessions.TryGetValue(grant.Holder, out TunerSession? host))
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The session '{grant.Holder}' that '{request.SessionId}' would have ridden on the device '{grant.DeviceId}' is not reading it."
            );
        }

        if (host.EndsAt <= now)
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The session '{host.SessionId}' that '{request.SessionId}' would read the device '{grant.DeviceId}' through stops at {host.EndsAt:O}, so there is no window left to share."
            );
        }

        if (!host.Broadcaster.TrySubscribe(SeatFor(request.Purpose), out SessionSubscription? seat))
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceBusy,
                $"The session '{host.SessionId}' on the device '{grant.DeviceId}' carries {host.Broadcaster.SubscriberLimit} readers at a time and they are all taken."
            );
        }

        return await OpenAsync(
            request,
            claim,
            grant.DeviceId,
            new PiggybackTunerDevice(host, seat),
            directory,
            now,
            endsAt,
            holds: false,
            tuned: false,
            ridesOn: host,
            seat: seat
        );
    }

    private async Task<SessionStart> TakeTheSeatAsync(
        StartSessionRequest request,
        PoolGrant grant,
        TunerClaim claim,
        string? directory,
        DateTimeOffset now,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken
    )
    {
        TunerReadiness readiness = await pool.AwaitReadyAsync(
            grant.DeviceId,
            PatienceOfARiderOn(grant.DeviceId),
            request.SessionId,
            cancellationToken
        );

        if (readiness.Leads)
        {
            return await LeadAsync(request, claim, directory, now, endsAt, cancellationToken);
        }

        if (readiness is { Ready: false, Refusal: { } undone })
        {
            claim.Leave();

            return SessionStart.Refused(undone.Refusal, undone.Detail);
        }

        if (!readiness.Ready || !sessions.TryGetValue(grant.Outgoing, out TunerSession? outgoing))
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The session '{grant.Outgoing}' that '{request.SessionId}' would have taken the device '{grant.DeviceId}' from is not reading it."
            );
        }

        if (outgoing.EndsAt <= now)
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The session '{outgoing.SessionId}' reading the device '{grant.DeviceId}' stops at {outgoing.EndsAt:O}, so it is leaving rather than handing the tuner on."
            );
        }

        if (pool.DeviceOf(grant.DeviceId) is not { } held)
        {
            claim.Leave();

            return SessionStart.Refused(
                SessionRefusal.DeviceUnavailable,
                $"The device '{grant.DeviceId}' was let go before '{request.SessionId}' could take it over."
            );
        }

        ITunerDevice tuner = new LeasedTunerDevice(held);

        return await OpenAsync(
            request,
            claim,
            grant.DeviceId,
            tuner,
            directory,
            now,
            endsAt,
            holds: true,
            tuned: false,
            demoting: outgoing
        );
    }

    private static SubscriberKind SeatFor(SessionPurpose purpose) =>
        purpose is SessionPurpose.Recording
            ? SubscriberKind.Recording
            : SubscriberKind.Piggyback;

    private async Task<bool> DemoteAsync(TunerSession outgoing, TunerSession taking)
    {
        if (
            !taking.Broadcaster.TrySubscribe(
                SubscriberKind.Piggyback,
                out SessionSubscription? seat
            )
            || !await outgoing.ReadFromInsteadAsync(
                new PiggybackTunerDevice(taking, seat),
                taking,
                seat,
                handOver
            )
        )
        {
            return false;
        }

        pool.SeatTaken(taking.DeviceId, taking.SessionId);

        logger.LogInformation(
            "Session {SessionId} handed the tuner {DeviceId} to the recording {Recording} and reads the stream through it from here on.",
            outgoing.SessionId.Value,
            outgoing.DeviceId,
            taking.SessionId.Value
        );

        return true;
    }

    private async Task<SessionStart> OpenAsync(
        StartSessionRequest request,
        TunerClaim claim,
        string deviceId,
        ITunerDevice tunerDevice,
        string? directory,
        DateTimeOffset now,
        DateTimeOffset endsAt,
        bool holds,
        bool tuned,
        TunerSession? ridesOn = null,
        SessionSubscription? seat = null,
        TunerSession? demoting = null
    )
    {
        SessionId sessionId = request.SessionId;
        string? claimed = null;

        if (request.RecordingId is { } recordingId)
        {
            if (!writing.TryAdd(recordingId, sessionId))
            {
                claim.Leave();
                tunerDevice.Dispose();

                return SessionStart.Refused(
                    SessionRefusal.RecordingAlreadyExists,
                    WhyTheRecordingIsSpokenFor(recordingId)
                );
            }

            claimed = recordingId;
        }

        IRecordingWriter? writer = null;
        if (directory is not null)
        {
            SessionStart? refusal = TryOpenRecording(
                directory,
                claim,
                sessionId,
                request.OutputRoot!,
                request.RecordingId!,
                out writer
            );

            if (refusal is not null)
            {
                LetGoOfTheRecording(claimed, sessionId);
                tunerDevice.Dispose();

                return refusal;
            }
        }

        TunerSession session;

        try
        {
            session = new TunerSession(
                sessionId,
                request.Purpose,
                deviceId,
                tunerDevice,
                now,
                endsAt,
                timeProvider,
                writer,
                logger: logger,
                outputRoot: request.OutputRoot,
                recordingId: request.RecordingId,
                diagnostics: diagnostics,
                watch: Watch(request.Purpose),
                tune: request.Tune,
                ridesOn: ridesOn,
                seat: seat,
                demuxBufferBytes: configuration.Tuner?.DemuxBufferBytes
                    ?? TunerSettings.DefaultDemuxBufferBytes,
                takesTheSeatFromAnother: demoting is not null
            );
        }
        catch
        {
            tunerDevice.Dispose();
            writer?.Dispose();
            LetGoOfTheRecording(claimed, sessionId);

            throw;
        }

        try
        {
            return await LaunchAsync(request, claim, session, claimed, deviceId, holds, tuned, demoting);
        }
        catch
        {
            Forsake(session, claimed);

            throw;
        }
    }

    private async Task<SessionStart> LaunchAsync(
        StartSessionRequest request,
        TunerClaim claim,
        TunerSession session,
        string? claimed,
        string deviceId,
        bool holds,
        bool tuned,
        TunerSession? demoting
    )
    {
        SessionId sessionId = request.SessionId;

        lock (drainGate)
        {
            if (draining)
            {
                claim.Leave();
                LetGoOfTheRecording(claimed, sessionId);
                session.Dispose();

                return SessionStart.Refused(
                    SessionRefusal.Draining,
                    "The driver is shutting down, so no session can start."
                );
            }

            if (!sessions.TryAdd(sessionId, session))
            {
                claim.Leave();
                LetGoOfTheRecording(claimed, sessionId);
                session.Dispose();

                return SessionStart.Refused(
                    SessionRefusal.DuplicateSession,
                    $"The session '{sessionId}' already exists."
                );
            }

            tunings[sessionId] = TuningKey.Of(request);
        }

        if (!pool.Confirm(deviceId, sessionId))
        {
            Abandon(session, claimed, claim);

            return SessionStart.Refused(
                SessionRefusal.DeviceBusy,
                $"The device '{deviceId}' was taken from '{sessionId}' before it could start."
            );
        }

        session.Ending += PassTheTunerOn;
        session.Ended += Forget;

        try
        {
            session.Start();
        }
        catch (Exception error)
        {
            bool stoppedBeforeItStarted = session.StopReason is SessionStopReason.Preempted or SessionStopReason.Requested;

            Abandon(session, claimed, claim);

            if (stoppedBeforeItStarted && draining)
            {
                return SessionStart.Refused(
                    SessionRefusal.Draining,
                    "The driver is shutting down, so no session can start."
                );
            }

            return !stoppedBeforeItStarted
                ? SessionStart.Refused(
                    SessionRefusal.DeviceUnavailable,
                    $"The session '{sessionId}' could not be started: {error.Message}"
                )
                : SessionStart.Refused(
                    SessionRefusal.DeviceBusy,
                    $"The device '{deviceId}' was taken from '{sessionId}' before it could start."
                );
        }

        if (demoting is { } outgoing)
        {
            if (!await DemoteAsync(outgoing, session))
            {
                Abandon(session, claimed, claim);

                return SessionStart.Refused(
                    SessionRefusal.DeviceBusy,
                    $"The device '{deviceId}' was asked for '{sessionId}', and '{outgoing.SessionId}' did not let go of it within {handOver}."
                );
            }

            session.TheSeatIsYours();
        }

        if (holds)
        {
            pool.Ready(deviceId, sessionId);
        }

        if (tuned)
        {
            events?.Signal(DriverEvents.SessionTuned);
        }

        reception?.Know(TuningKey.Of(request));
        Announce();
        claim.PassTo(session);

        return SessionStart.Started(session);
    }

    private void Abandon(TunerSession session, string? claimed, TunerClaim claim)
    {
        Forsake(session, claimed);
        claim.Leave();
    }

    private void Forsake(TunerSession session, string? claimed)
    {
        sessions.TryRemove(
            new KeyValuePair<SessionId, TunerSession>(session.SessionId, session)
        );
        tunings.TryRemove(session.SessionId, out _);
        session.Ending -= PassTheTunerOn;
        session.Ended -= Forget;
        LetGoOfTheRecording(claimed, session.SessionId);
        session.Dispose();
    }

    public IDisposable? ClaimForErasure(string recordingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);

        return writing.TryAdd(recordingId, default) ? new ErasureClaim(this, recordingId) : null;
    }

    private string WhyTheRecordingIsSpokenFor(string recordingId)
    {
        SessionId holder = HolderOf(recordingId);

        return holder.IsUnset
            ? $"The recording '{recordingId}' is being thrown away, so nothing may start writing to it."
            : $"The session '{holder}' is already writing the recording '{recordingId}'; a second one would split it across two files and leave one of them an orphan.";
    }

    private SessionId HolderOf(string recordingId) =>
        writing.TryGetValue(recordingId, out SessionId holder) ? holder : default;

    private void LetGoOfTheErasure(string recordingId) =>
        writing.TryRemove(new KeyValuePair<string, SessionId>(recordingId, default));

    private sealed class ErasureClaim(TunerSessionManager manager, string recordingId) : IDisposable
    {
        public void Dispose() => manager.LetGoOfTheErasure(recordingId);
    }

    private void LetGoOfTheRecording(string? recordingId, SessionId sessionId)
    {
        if (recordingId is not null)
        {
            writing.TryRemove(new KeyValuePair<string, SessionId>(recordingId, sessionId));
        }
    }

    private SessionStart? TryOpenRecording(
        string directory,
        TunerClaim claim,
        SessionId sessionId,
        string outputRoot,
        string recordingId,
        out IRecordingWriter? writer
    )
    {
        writer = null;

        try
        {
            writer = writerFactory.Open(directory, recordingId);

            return null;
        }
        catch (Exception error)
        {
            claim.Leave();

            logger.LogError(
                error,
                "The recording {RecordingId} for {SessionId} could not be opened under {OutputRoot}.",
                recordingId,
                sessionId.Value,
                outputRoot
            );

            return SessionStart.Refused(
                SessionRefusal.OutputUnavailable,
                $"The recording '{recordingId}' could not be opened under the output root '{outputRoot}'; the driver log says why."
            );
        }
    }

    public bool TryGet(SessionId sessionId, [NotNullWhen(true)] out TunerSession? session)
    {
        if (sessions.TryGetValue(sessionId, out session))
        {
            return true;
        }

        session = WhatEndedUnder(sessionId);

        return session is not null;
    }

    private TunerSession? WhatEndedUnder(SessionId sessionId) =>
        ended.LastOrDefault(candidate => candidate.SessionId == sessionId);

    private static bool CarriesOn(TunerSession ended, StartSessionRequest request) =>
        request.Purpose is SessionPurpose.Recording
        && request.RecordingId is { Length: > 0 } recordingId
        && string.Equals(ended.RecordingId, recordingId, StringComparison.Ordinal);

    public SessionExtension Extend(SessionId sessionId, ExtendSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!sessions.TryGetValue(sessionId, out TunerSession? session))
        {
            return ended.Any(candidate => candidate.SessionId == sessionId)
                ? SessionExtension.Refused(
                    SessionExtendOutcome.AlreadyEnded,
                    $"The session '{sessionId}' has already ended, so there is no end left to move."
                )
                : SessionExtension.Refused(
                    SessionExtendOutcome.NoSuchSession,
                    $"This driver holds no session called '{sessionId}'."
                );
        }

        if (session.Purpose is not (SessionPurpose.Recording or SessionPurpose.Live))
        {
            return SessionExtension.Refused(
                SessionExtendOutcome.NotARecording,
                $"The session '{sessionId}' is a {SessionPurposeConverter.WireName(session.Purpose)} one, and this driver holds its end to the limit its purpose is given."
            );
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<string> problems = request.Validate(session.EndsAt, now);

        if (problems.Count > 0)
        {
            return SessionExtension.Refused(
                SessionExtendOutcome.NotAnExtension,
                string.Join(" ", problems)
            );
        }

        DateTimeOffset endsAt = HeldNoFurtherThan(session, request.EndsAt, now);

        if (endsAt <= session.EndsAt)
        {
            return SessionExtension.Extended(session);
        }

        if (!session.Extend(endsAt))
        {
            return SessionExtension.Refused(
                SessionExtendOutcome.AlreadyEnded,
                $"The session '{sessionId}' is {session.State} and is no longer taking a later end."
            );
        }

        logger.LogInformation(
            "Session {SessionId} now runs until {EndsAt}.",
            sessionId.Value,
            session.EndsAt
        );

        events?.Signal(DriverEvents.Sessions);

        return SessionExtension.Extended(session);
    }

    private DateTimeOffset HeldNoFurtherThan(
        TunerSession session,
        DateTimeOffset asked,
        DateTimeOffset now
    )
    {
        if (session.Purpose is not SessionPurpose.Live)
        {
            return asked;
        }

        DateTimeOffset latest = now.AddMinutes(configuration.LiveSessionMinutes);

        if (asked <= latest)
        {
            return asked;
        }

        logger.LogInformation(
            "Session {SessionId} asked to be held until {Asked} and was cut to {Granted}.",
            session.SessionId.Value,
            asked,
            latest
        );

        return latest;
    }

    public async Task<SessionStopOutcome> StopAsync(
        SessionId sessionId,
        string reason,
        CancellationToken cancellationToken
    )
    {
        if (!sessions.TryGetValue(sessionId, out TunerSession? session))
        {
            return ended.Any(candidate => candidate.SessionId == sessionId)
                ? SessionStopOutcome.AlreadyEnded
                : SessionStopOutcome.NoSuchSession;
        }

        logger.LogInformation(
            "Session {SessionId} was asked to stop: {Reason}",
            sessionId.Value,
            reason
        );
        session.Stop();

        try
        {
            await session.Completion.WaitAsync(letGo, timeProvider, cancellationToken);

            return SessionStopOutcome.Stopped;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return SessionStopOutcome.Stopping;
        }
    }

    private void PassTheTunerOn(TunerSession leaving)
    {
        if (leaving.StopReason is SessionStopReason.DeviceFailed)
        {
            return;
        }

        if (pool.DeviceOf(leaving.DeviceId) is not { } tuner)
        {
            return;
        }

        foreach (SessionId candidate in pool.WhoElseIsReadingThrough(leaving.SessionId))
        {
            if (!IsReadingThrough(candidate, leaving, out TunerSession? next))
            {
                continue;
            }

            if (!pool.HandTheTunerOn(leaving.SessionId, candidate))
            {
                return;
            }

            if (
                next.ReadFromInsteadOnceThisStreamEnds(new LeasedTunerDevice(tuner), null, null)
            )
            {
                logger.LogInformation(
                    "Session {SessionId} on {DeviceId} is ending, so {Successor} takes the tuner and everything else still on it reads the stream through that one from here on.",
                    leaving.SessionId.Value,
                    leaving.DeviceId,
                    candidate.Value
                );

                TheRestReadThrough(next, leaving);
            }

            return;
        }
    }

    private bool IsReadingThrough(
        SessionId reader,
        TunerSession host,
        [NotNullWhen(true)] out TunerSession? session
    ) =>
        sessions.TryGetValue(reader, out session)
        && session.State is SessionState.Active
        && ReferenceEquals(session.RidesOn, host);

    private void TheRestReadThrough(TunerSession holder, TunerSession leaving)
    {
        foreach (SessionId other in pool.WhoElseIsReadingThrough(holder.SessionId))
        {
            if (
                !IsReadingThrough(other, leaving, out TunerSession? rider)
                || !holder.Broadcaster.TrySubscribe(
                    SeatFor(rider.Purpose),
                    out SessionSubscription? seat
                )
            )
            {
                continue;
            }

            if (
                !rider.ReadFromInsteadOnceThisStreamEnds(
                    new PiggybackTunerDevice(holder, seat),
                    holder,
                    seat
                )
            )
            {
                holder.Broadcaster.Unsubscribe(seat);
            }
        }
    }

    private void Forget(TunerSession session)
    {
        ended.Enqueue(session);

        while (ended.Count > RetainedSessions && ended.TryDequeue(out _))
        { }

        try
        {
            NoteHowItEnded(session);
        }
        finally
        {
            LetGoOfTheRecording(session.RecordingId, session.SessionId);

            pool.Leave(session.SessionId);
            pool.Sweep();

            sessions.TryRemove(new KeyValuePair<SessionId, TunerSession>(session.SessionId, session));
        }

        Announce();
    }

    private void NoteHowItEnded(TunerSession session)
    {
        if (tunings.TryRemove(session.SessionId, out TuningKey? tuning))
        {
            NoteTheReceptionOf(session, tuning);

            if (session.State is SessionState.Stopped)
            {
                ForgiveTuneFailures(session.DeviceId);
            }
            else if (
                session.FailureCause is DvbDeviceException
                {
                    Failure: TuningFailure.NoLock or TuningFailure.LockedWithoutData
                } channel
            )
            {
                RecordTuneFailure(session.DeviceId, tuning, channel, session.Purpose);
            }
        }

        if (session.StopReason is SessionStopReason.DeviceFailed)
        {
            FaultAfterFailing(session);
        }
    }

    private void NoteTheReceptionOf(TunerSession session, TuningKey tuning)
    {
        if (session.RidesOn is not null || session.StopReason is SessionStopReason.DeviceFailed)
        {
            return;
        }

        bool locked = session.HasHeldTheLock
            || session.FailureCause is not DvbDeviceException { Failure: TuningFailure.NoLock or TuningFailure.LockedWithoutData };

        NoteTheReception(session.DeviceId, tuning, locked, locked ? Median.Of(session.CarrierToNoiseReadings) : null);
    }

    private void NoteTheReception(string deviceId, TuningKey tuning, bool locked, double? carrierToNoise)
    {
        ReceptionChannel channel = ReceptionChannel.Of(tuning);

        reception?.Note(
            deviceId,
            channel,
            new ReceptionResult(timeProvider.GetUtcNow(), locked, carrierToNoise),
            () => IsReceivedOnAnotherTuner(channel, deviceId)
        );
    }

    public bool IsReceivedOnAnotherTuner(ReceptionChannel channel, string deviceId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        return sessions.Values.Any(other =>
            other.DeviceId != deviceId
            && other.RidesOn is null
            && other.State is SessionState.Active
            && HeldTheLockLately(other, now)
            && tunings.TryGetValue(other.SessionId, out TuningKey? receiving)
            && ReceptionChannel.Of(receiving) == channel
        );
    }

    private bool HeldTheLockLately(TunerSession session, DateTimeOffset now) =>
        session.Quality is { HasLock: true } reading
        && now - reading.LockReadAt <= Interval(session.Purpose) * LockReadingsThatCountAsNow;

    private void FaultAfterFailing(TunerSession session)
    {
        string deviceId = session.DeviceId;
        string fault =
            $"The device failed while serving '{session.SessionId}': "
            + (session.FailureCause?.Message ?? "no cause was recorded.");

        if (FaultUntilItOpens(deviceId, new TunerFault(fault, null, DeviceFaultKind.Failed)) is not { } plan)
        {
            logger.LogError(
                "The device {DeviceId} failed while serving {SessionId} and {NotTriedAgain}",
                deviceId,
                session.SessionId.Value,
                NotTriedAgain()
            );

            return;
        }

        if (!plan.Relapsed)
        {
            logger.LogWarning(
                "The device {DeviceId} failed while serving {SessionId}, so it is not handed out until it opens again; it is tried again at {NextTryAt}.",
                deviceId,
                session.SessionId.Value,
                plan.NextTryAt
            );

            return;
        }

        logger.LogError(
            "The device {DeviceId} failed again while serving {SessionId} within {RelapseMinutes} minutes of being handed back out, so it waits longer than last time and is tried again at {NextTryAt}.",
            deviceId,
            session.SessionId.Value,
            RelapseMinutes,
            plan.NextTryAt
        );
    }

    private RecheckPlan? FaultUntilItOpens(string deviceId, TunerFault fault)
    {
        pool.Fault(deviceId, fault);

        return TryAgainOnceFaulted(deviceId, fault);
    }

    private RecheckPlan? TryAgainOnceFaulted(string deviceId, TunerFault fault)
    {
        healthChangedAt[deviceId] = timeProvider.GetUtcNow();

        events?.Signal(DriverEvents.TunerHealthChanged);
        events?.Signal(DriverEvents.Tuners);

        if (recheck is null || draining || DeviceNamed(deviceId) is not { } device)
        {
            return null;
        }

        RecheckPlan? plan = recheck.Schedule(device, Restore);

        if (plan is { Relapsed: true } && fault.Kind.Kind is TunerFaultKind.DeviceFailed)
        {
            pool.Fault(
                deviceId,
                fault with
                {
                    Detail = fault.Detail
                        + $" It had been handed back out less than {RelapseMinutes} minutes earlier, so it waits longer than last time before it is tried again.",
                    Kind = DeviceFaultKind.FailedAgain,
                }
            );
        }

        return plan;
    }

    private static double RelapseMinutes => FaultedTunerRecheck.RelapseWindow.TotalMinutes;

    private void Restore(string deviceId)
    {
        lock (streakGate)
        {
            tuneFailures.Remove(deviceId);
        }

        pool.Restore(deviceId);
        healthChangedAt[deviceId] = timeProvider.GetUtcNow();

        logger.LogInformation(
            "The faulted device {DeviceId} opened when it was tried again, so it is no longer faulted and is handed out again.",
            deviceId
        );

        events?.Signal(DriverEvents.TunerHealthChanged);
        events?.Signal(DriverEvents.Tuners);
    }

    private DeviceSettings? DeviceNamed(string deviceId) =>
        (configuration.Devices ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate?.Id, deviceId, StringComparison.Ordinal)
        );

    private SignalQualityWatch Watch(SessionPurpose purpose) =>
        new(
            Interval(purpose),
            (_, _) =>
            {
                events?.Signal(DriverEvents.SessionLockLost);
                events?.Signal(DriverEvents.Tuners);
            }
        );

    private TimeSpan Interval(SessionPurpose purpose)
    {
        TimeSpan configured = configuration.Tuner?.SignalQualityInterval ?? SignalQualityReader.DefaultInterval;

        return purpose is SessionPurpose.Scan && SignalQualityReader.WhileWalkingChannels < configured
            ? SignalQualityReader.WhileWalkingChannels
            : configured;
    }

    private void Announce()
    {
        events?.Signal(DriverEvents.Sessions);
        events?.Signal(DriverEvents.Tuners);
    }

    private void RecordTuneFailure(
        string deviceId,
        TuningKey tuning,
        DvbDeviceException cause,
        SessionPurpose purpose
    )
    {
        bool first;
        TuningKey[] channels;

        lock (streakGate)
        {
            first = !tuneFailures.TryGetValue(deviceId, out List<TuningKey>? counted);
            counted ??= tuneFailures[deviceId] = [];

            if (purpose is not SessionPurpose.Scan && !counted.Any(seen => OnTheSameChannel(seen, tuning)))
            {
                counted.Add(tuning);
            }

            channels = [.. counted];
        }

        if (channels.Length < DifferentChannelsThatFaultTheDevice)
        {
            if (first)
            {
                healthChangedAt[deviceId] = timeProvider.GetUtcNow();
                events?.Signal(DriverEvents.TunerHealthChanged);
                events?.Signal(DriverEvents.Tuners);
            }

            return;
        }

        string fault =
            $"The device failed to receive {string.Join(", ", channels)} one after another without delivering"
            + $" anything in between; the last failure was: {cause.Message}";
        RecheckPlan? plan = FaultUntilItOpens(
            deviceId,
            new TunerFault(fault, TuningFailureTitles.Of(cause.Failure), DeviceFaultKind.RepeatedTuneFailure)
        );

        if (plan is null)
        {
            logger.LogError(
                "The device {DeviceId} failed to receive {Channels} one after another and {NotTriedAgain}",
                deviceId,
                string.Join(", ", channels),
                NotTriedAgain()
            );

            return;
        }

        logger.LogWarning(
            "The device {DeviceId} failed to receive {Channels} one after another, so it is not handed out until it opens again; it is tried again at {NextTryAt}.",
            deviceId,
            string.Join(", ", channels),
            plan.NextTryAt
        );
    }

    private string NotTriedAgain() =>
        draining
            ? "the driver is shutting down, so it is not tried again."
            : "nothing is set to try it again, so it stays faulted until the driver restarts.";

    private static bool OnTheSameChannel(TuningKey one, TuningKey other) =>
        one.Kind == other.Kind && one.PhysicalChannel == other.PhysicalChannel;

    private void ForgiveTuneFailures(string deviceId)
    {
        bool forgiven;

        lock (streakGate)
        {
            forgiven = tuneFailures.Remove(deviceId);
        }

        if (forgiven && pool.FaultOf(deviceId) is null)
        {
            healthChangedAt[deviceId] = timeProvider.GetUtcNow();
            events?.Signal(DriverEvents.TunerHealthChanged);
            events?.Signal(DriverEvents.Tuners);
        }
    }

    public bool IsFailingToTune(string deviceId)
    {
        lock (streakGate)
        {
            return tuneFailures.ContainsKey(deviceId);
        }
    }

    public void Fault(TunerContradiction contradiction)
    {
        ArgumentNullException.ThrowIfNull(contradiction);

        Fault(
            contradiction.DeviceId,
            new TunerFault(
                contradiction.Detail,
                null,
                DeviceFaultKind.Disagreeing(contradiction.Declared, contradiction.Receives)
            )
        );
    }

    private void Fault(string deviceId, TunerFault fault)
    {
        recheck?.Forget(deviceId);
        pool.Fault(deviceId, fault);
        healthChangedAt[deviceId] = timeProvider.GetUtcNow();

        events?.Signal(DriverEvents.TunerHealthChanged);
        events?.Signal(DriverEvents.Tuners);
    }

    public IReadOnlyList<RestingTuner> RestingTuners() => draining ? [] : pool.Resting();

    public MeasuringTicket? BeginMeasuring(string deviceId, TuningKey tuning) =>
        draining ? null : pool.BeginMeasuring(deviceId, tuning);

    public bool EndMeasuring(MeasuringTicket ticket, Exception? deviceFailure = null)
    {
        if (deviceFailure is null)
        {
            return pool.EndMeasuring(ticket);
        }

        var fault = new TunerFault(
            $"The device failed while how well it receives was being measured: {deviceFailure.Message}",
            null,
            DeviceFaultKind.Failed
        );

        if (!pool.EndMeasuring(ticket, fault))
        {
            return false;
        }

        RecheckPlan? plan = TryAgainOnceFaulted(ticket.DeviceId, fault);

        logger.LogWarning(
            "The device {DeviceId} failed while how well it receives was being measured, so it is not handed out until it opens again; {NextTry}",
            ticket.DeviceId,
            plan is null ? NotTriedAgain() : $"it is tried again at {plan.NextTryAt:O}."
        );

        return true;
    }

    public void LookAgainAt(string deviceId)
    {
        try
        {
            Look(configuration, presence, deviceId);
        }
        catch (Exception failed)
        {
            logger.LogWarning(failed, "Whether the tuner {DeviceId} is still plugged in could not be looked at.", deviceId);
        }
    }

    public bool IsClaimed(string deviceId) => pool.IsHeld(deviceId);

    public IReadOnlyList<string> OccupiedDevices() => pool.Occupied();

    public IReadOnlyList<TunerAllocationView> Allocations() => pool.Snapshot();

    public void Looked(DeviceSettings device, bool present)
    {
        if (device.Id is { } deviceId)
        {
            pool.Looked(deviceId, present);
        }
    }

    private static Func<string, TuningKey, ReceptionStanding>? StandingIn(ReceptionBook? book) =>
        book is null ? null : (deviceId, tuning) => book.StandingOf(deviceId, ReceptionChannel.Of(tuning));

    private static bool? Look(DriverConfiguration configuration, Func<DeviceSettings, bool?>? presence, string deviceId) =>
        presence is not null
        && (configuration.Devices ?? []).FirstOrDefault(candidate => candidate?.Id == deviceId) is { } device
            ? presence(device)
            : null;

    private static bool IsKeptOpen(DriverConfiguration configuration, ITunerDeviceCheck? check, string deviceId) =>
        check is not null
        && (configuration.Devices ?? []).FirstOrDefault(candidate => candidate?.Id == deviceId) is { } device
        && check.IsKeptOpen(device);

    private static void LetGo(DriverConfiguration configuration, ITunerDeviceCheck? check, string deviceId)
    {
        if (check is null)
        {
            return;
        }

        if ((configuration.Devices ?? []).FirstOrDefault(candidate => candidate?.Id == deviceId) is { } device)
        {
            check.LetGo(device);
        }
    }

    public bool IsEnabled(DeviceSettings device) =>
        device.Id is { } deviceId && pool.IsEnabled(deviceId) is { } enabled ? enabled : device.Enabled;

    public bool IsToggled(DeviceSettings device) => IsEnabled(device) != device.Enabled;

    public bool Turn(string deviceId, bool disabled)
    {
        if (DeviceNamed(deviceId) is null)
        {
            return false;
        }

        pool.Turn(deviceId, !disabled);
        healthChangedAt[deviceId] = timeProvider.GetUtcNow();

        events?.Signal(DriverEvents.TunerHealthChanged);
        events?.Signal(DriverEvents.Tuners);

        return true;
    }

    public DateTimeOffset? HealthChangedAt(string deviceId) =>
        healthChangedAt.TryGetValue(deviceId, out DateTimeOffset changed) ? changed : null;

    public bool IsFaulted(string deviceId, [NotNullWhen(true)] out string? detail)
    {
        detail = pool.FaultOf(deviceId)?.Detail;

        return detail is not null;
    }

    public string? FaultTitleOf(string deviceId) => pool.FaultOf(deviceId)?.Title;

    public DeviceFaultKind? FaultKindOf(string deviceId) => pool.FaultOf(deviceId)?.Kind;

    private static TunerKind KindOf(StartSessionRequest request) =>
        request.Tune?.Kind ?? request.Tuning.Kind;

    private static bool Matches(DeviceKind device, TunerKind requested) =>
        (device, requested) switch
        {
            (DeviceKind.Terrestrial, TunerKind.Terrestrial) => true,
            (DeviceKind.Satellite, TunerKind.Satellite) => true,
            _ => false,
        };
}
