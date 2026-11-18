using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Sessions;

public enum PoolVerdict
{
    Granted,
    Shared,
    Swapped,
    NoDeviceFree,
    DeviceBusy,
    DisabledDevice,
    FaultedDevice,
    WrongDeviceKind,
    NoDeviceOfThatKind,
    DeviceUnavailable,
}

public sealed record PoolRequest(
    SessionId SessionId,
    SessionPurpose Purpose,
    TuningKey Tuning,
    string? DeviceId,
    IReadOnlyList<string> Candidates
)
{
    public int Priority => SessionPriority.Of(Purpose);

    public IReadOnlyList<string> Allowed => DeviceId is { } named ? [named] : Candidates;

    public bool NamedServesTheKind { get; init; } = true;

    public TunerKind Serves { get; init; } = TunerKind.Unspecified;

    public TunerKind Kind => Serves is TunerKind.Unspecified ? Tuning.Kind : Serves;
}

public sealed record PoolGrant(
    PoolVerdict Verdict,
    string DeviceId,
    SessionId Holder,
    bool NeedsTuning,
    IReadOnlyList<SessionId> Displaced,
    string Detail,
    SessionId Outgoing = default,
    Task<bool>? Clearing = null
)
{
    private static readonly Task<bool> AlreadyClear = Task.FromResult(true);

    public static PoolGrant Refused(PoolVerdict verdict, string detail) =>
        new(verdict, string.Empty, default, false, [], detail);

    public bool IsGranted =>
        Verdict is PoolVerdict.Granted or PoolVerdict.Shared or PoolVerdict.Swapped;

    public Task<bool> Cleared => Clearing ?? AlreadyClear;
}

public sealed record TunerRefusal(SessionRefusal Refusal, string Detail);

public sealed record TunerReadiness(bool Ready, TunerRefusal? Refusal, bool Leads = false);

public sealed record TunerClearance(Task<bool> Cleared, DateTimeOffset Since);

public sealed record OpenTicket(string DeviceId, object Token, Task<TunerRefusal?> Outcome, bool Starts, bool Tunes);

public sealed record TunerFault(string Detail, string? Title, DeviceFaultKind Kind);

public sealed record TunerAllocationView(
    string DeviceId,
    TunerAllocationState State,
    TuningKey? Tuning,
    SessionId Holder,
    IReadOnlyList<SessionId> Sinks,
    IReadOnlyList<SessionId> Outgoing,
    bool DeviceOpen,
    bool PreviousDeviceOpen,
    bool OpeningInFlight,
    bool PreviousOpeningInFlight,
    bool Enabled,
    bool Faulted
);

public sealed class TunerPool(
    TimeProvider timeProvider,
    TimeSpan? grace = null,
    IEnumerable<(string DeviceId, bool Enabled)>? declared = null,
    Action<string>? letGo = null,
    ILogger? logger = null,
    Func<string, bool?>? look = null
) : IDisposable
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    public const int RiderLimit = SessionBroadcaster.DefaultSubscriberLimit;

    private sealed record Sink(SessionId SessionId, int Priority);

    private sealed class Occupancy(TuningKey tuning, SessionId holder)
    {
        public TuningKey Tuning { get; } = tuning;

        public SessionId Holder { get; set; } = holder;

        public SessionId Opener { get; set; } = holder;

        public SessionId? Displacing { get; set; }

        public List<Sink> Sinks { get; } = [];

        public ITunerDevice? Device { get; set; }

        public bool Established { get; set; }

        public TaskCompletionSource<TunerRefusal?>? Opening { get; set; }

        public bool InFlight { get; set; }

        public TaskCompletionSource Ready { get; set; } = Unset();

        public TaskCompletionSource Stirred { get; set; } = Unset();

        public int Priority =>
            Sinks.Count is 0 ? SessionPriority.Unknown : Sinks.Max(sink => sink.Priority);

        public bool Has(SessionId sessionId) => Sinks.Exists(sink => sink.SessionId == sessionId);
    }

    private sealed class Entry(string deviceId, bool enabled)
    {
        public string DeviceId { get; } = deviceId;

        public TunerAllocationState State { get; set; } =
            enabled ? TunerAllocationState.Free : TunerAllocationState.Disabled;

        public bool Enabled { get; set; } = enabled;

        public bool Present { get; set; } = true;

        public TunerFault? Fault { get; set; }

        public Occupancy? Current { get; set; }

        public Occupancy? Previous { get; set; }

        public TaskCompletionSource<bool>? Handover { get; set; }

        public DateTimeOffset? IdleSince { get; set; }

        public Exception? HeldBack { get; set; }

        public TuningKey? HeldBackFor { get; set; }

        public DateTimeOffset HeldBackUntil { get; set; }

        public Task Quiet { get; set; } = Task.CompletedTask;

        public int Closing { get; set; }

        public int InFlight { get; set; }

        public DateTimeOffset HandoverSince { get; set; }

        public bool IsOccupied =>
            State is TunerAllocationState.Tuning
                or TunerAllocationState.InUse
                or TunerAllocationState.HandingOver;
    }

    private readonly Dictionary<string, Entry> entries = Declare(declared);
    private readonly Dictionary<SessionId, Entry> bySink = [];
    private readonly Dictionary<SessionId, TunerRefusal> turnedAway = [];
    private readonly List<string> refusedTransitions = [];
    private readonly Lock gate = new();
    private readonly TimeSpan linger = grace ?? DefaultGrace;
    private ITimer? sweeper;
    private bool disposed;
    private int refusals;

    public TimeSpan Grace => linger;

    public int RefusedTransitions => Volatile.Read(ref refusals);

    private static Dictionary<string, Entry> Declare(IEnumerable<(string DeviceId, bool Enabled)>? declared)
    {
        Dictionary<string, Entry> declaredEntries = new(StringComparer.Ordinal);

        foreach ((string deviceId, bool enabled) in declared ?? [])
        {
            declaredEntries[deviceId] = new Entry(deviceId, enabled);
        }

        return declaredEntries;
    }

    public PoolGrant Acquire(PoolRequest request)
    {
        if (Decide(request, look is not null, out IReadOnlyList<string> absent) is { } grant)
        {
            return grant;
        }

        foreach (string deviceId in absent)
        {
            if (LookAt(deviceId) is { } present)
            {
                Looked(deviceId, present);
            }
        }

        return Decide(request, false, out _)!;
    }

    private bool? LookAt(string deviceId)
    {
        try
        {
            return look?.Invoke(deviceId);
        }
        catch (Exception failed)
        {
            logger?.LogWarning(
                failed,
                "Whether the tuner {DeviceId} is plugged in could not be looked at, so it is taken as it was last seen.",
                deviceId
            );

            return null;
        }
    }

    private PoolGrant? Decide(PoolRequest request, bool mayLookAgain, out IReadOnlyList<string> absent)
    {
        List<Action> after = [];
        PoolGrant? grant;

        absent = [];

        lock (gate)
        {
            Expire(after);

            grant =
                Ineligible(request, mayLookAgain)
                ?? RideAlong(request, after)
                ?? TakeAFreeTuner(request)
                ?? TakeATunerNobodyIsOn(request, after);

            if (grant is null && mayLookAgain)
            {
                absent = Unplugged(request);
            }

            if (grant is null && absent.Count is 0)
            {
                grant =
                    TakeATunerFromSomethingLessImportant(request, after)
                    ?? TurnAway(request);
            }
        }

        Run(after);

        return grant;
    }

    private string[] Unplugged(PoolRequest request) =>
    [
        .. request
            .Allowed.Select(EntryOf)
            .Where(entry => entry.Enabled && entry.Fault is null && entry.State is TunerAllocationState.Absent)
            .Select(entry => entry.DeviceId),
    ];

    private PoolGrant? Ineligible(PoolRequest request, bool lookingAgain)
    {
        if (disposed)
        {
            return PoolGrant.Refused(PoolVerdict.NoDeviceFree, "The driver is shutting down, so no tuner is handed out.");
        }

        if (request.DeviceId is { } named)
        {
            return IneligibleNamed(EntryOf(named), request, lookingAgain);
        }

        Entry[] enabled = [.. request.Candidates.Select(EntryOf).Where(entry => entry.Enabled)];

        if (enabled.Length is 0)
        {
            return PoolGrant.Refused(
                PoolVerdict.NoDeviceOfThatKind,
                $"This driver has no enabled device that serves {request.Kind}."
            );
        }

        return enabled.All(entry => entry.Fault is not null)
            ? PoolGrant.Refused(
                PoolVerdict.FaultedDevice,
                $"Every device that serves {request.Kind} is faulted."
            )
            : null;
    }

    private static PoolGrant? IneligibleNamed(Entry entry, PoolRequest request, bool lookingAgain)
    {
        string named = entry.DeviceId;

        if (!entry.Enabled)
        {
            return PoolGrant.Refused(
                PoolVerdict.DisabledDevice,
                entry.IsOccupied
                    ? $"The device '{named}' is being taken out of service and is finishing the session it holds."
                    : $"The device '{named}' is disabled."
            );
        }

        if (entry.Fault is { } fault)
        {
            return PoolGrant.Refused(PoolVerdict.FaultedDevice, fault.Detail);
        }

        if (!request.NamedServesTheKind)
        {
            return PoolGrant.Refused(PoolVerdict.WrongDeviceKind, string.Empty);
        }

        return entry.State is TunerAllocationState.Absent && !lookingAgain
            ? PoolGrant.Refused(
                PoolVerdict.DeviceUnavailable,
                $"The device '{named}' is not plugged in."
            )
            : null;
    }

    private IEnumerable<Entry> Usable(PoolRequest request) =>
        request
            .Allowed.Select(EntryOf)
            .Where(entry => entry.Enabled && entry.Fault is null && entry.State is not TunerAllocationState.Absent);

    private PoolGrant? RideAlong(PoolRequest request, List<Action> after)
    {
        foreach (Entry entry in Usable(request))
        {
            if (entry.Current is not { } occupancy || occupancy.Tuning != request.Tuning)
            {
                continue;
            }

            if (entry.State is TunerAllocationState.Ready)
            {
                return Rehold(entry, occupancy, request);
            }

            if (!entry.IsOccupied)
            {
                continue;
            }

            if (occupancy.Sinks.Count is 0)
            {
                return Adopt(entry, occupancy, request);
            }

            if (request.Purpose is SessionPurpose.Recording && occupancy.Priority < request.Priority)
            {
                return TakeTheSeat(entry, occupancy, request);
            }

            if (occupancy.Sinks.Count - 1 >= RiderLimit)
            {
                return PoolGrant.Refused(
                    PoolVerdict.DeviceBusy,
                    $"The session '{occupancy.Holder}' on the device '{entry.DeviceId}' carries {RiderLimit} readers at a time and they are all taken."
                );
            }

            if (!Move(entry, TunerAllocationEvent.Ride))
            {
                continue;
            }

            Attach(entry, occupancy, request);

            return new PoolGrant(
                PoolVerdict.Shared,
                entry.DeviceId,
                occupancy.Holder,
                NeedsTuning: false,
                [],
                $"The tuner '{entry.DeviceId}' is already on {request.Tuning}, so '{request.SessionId}' rides the stream '{occupancy.Holder}' is reading."
            );
        }

        return null;
    }

    private PoolGrant? Adopt(Entry entry, Occupancy occupancy, PoolRequest request)
    {
        if (entry.State is not TunerAllocationState.Tuning || !Move(entry, TunerAllocationEvent.Ride))
        {
            return null;
        }

        occupancy.Holder = request.SessionId;
        occupancy.Opener = request.SessionId;
        occupancy.Displacing = null;
        Attach(entry, occupancy, request);

        return new PoolGrant(
            PoolVerdict.Granted,
            entry.DeviceId,
            request.SessionId,
            NeedsTuning: occupancy.Device is null,
            [],
            $"The tuner '{entry.DeviceId}' is being opened on {request.Tuning} for nobody any more, so '{request.SessionId}' takes the opening over."
        );
    }

    private PoolGrant? TakeTheSeat(Entry entry, Occupancy occupancy, PoolRequest request)
    {
        if (!Move(entry, TunerAllocationEvent.TakeSeat))
        {
            return null;
        }

        SessionId outgoing = occupancy.Holder;

        occupancy.Displacing = outgoing;
        occupancy.Holder = request.SessionId;
        Attach(entry, occupancy, request);

        return new PoolGrant(
            PoolVerdict.Swapped,
            entry.DeviceId,
            request.SessionId,
            NeedsTuning: false,
            [],
            $"The tuner '{entry.DeviceId}' is on {request.Tuning} for '{outgoing}', which a recording outranks, so '{request.SessionId}' reads the tuner and '{outgoing}' reads the stream through it.",
            outgoing
        );
    }

    private PoolGrant? Rehold(Entry entry, Occupancy occupancy, PoolRequest request)
    {
        if (!Move(entry, TunerAllocationEvent.Claim))
        {
            return null;
        }

        occupancy.Holder = request.SessionId;
        occupancy.Opener = request.SessionId;
        occupancy.Displacing = null;
        occupancy.Established = false;
        occupancy.Opening = null;
        occupancy.Ready = Unset();
        occupancy.Stirred = Unset();
        Attach(entry, occupancy, request);

        return new PoolGrant(
            PoolVerdict.Granted,
            entry.DeviceId,
            request.SessionId,
            NeedsTuning: false,
            [],
            $"The tuner '{entry.DeviceId}' was still on {request.Tuning}, so '{request.SessionId}' takes it back without tuning it again.",
            Clearing: ClearedOnce(entry.Quiet)
        );
    }

    private PoolGrant? TakeAFreeTuner(PoolRequest request)
    {
        foreach (Entry entry in Usable(request))
        {
            if (entry.State is not TunerAllocationState.Free || HoldsBack(entry, request))
            {
                continue;
            }

            if (!Move(entry, TunerAllocationEvent.Claim))
            {
                continue;
            }

            entry.Current = new Occupancy(request.Tuning, request.SessionId);
            Attach(entry, entry.Current, request);

            return new PoolGrant(
                PoolVerdict.Granted,
                entry.DeviceId,
                request.SessionId,
                NeedsTuning: true,
                [],
                $"The tuner '{entry.DeviceId}' is free and goes to '{request.SessionId}' for {request.Tuning}.",
                Clearing: ClearedOnce(entry.Quiet)
            );
        }

        return null;
    }

    private PoolGrant? TakeATunerNobodyIsOn(PoolRequest request, List<Action> after)
    {
        foreach (Entry entry in Usable(request))
        {
            if (entry.State is not TunerAllocationState.Ready || entry.Current is not { } held)
            {
                continue;
            }

            if (!Move(entry, TunerAllocationEvent.Claim))
            {
                continue;
            }

            Drop(entry, held.Device, after);
            entry.Current = new Occupancy(request.Tuning, request.SessionId);
            Attach(entry, entry.Current, request);

            return new PoolGrant(
                PoolVerdict.Granted,
                entry.DeviceId,
                request.SessionId,
                NeedsTuning: true,
                [],
                $"The tuner '{entry.DeviceId}' was being held for whoever came back to {held.Tuning}; nobody did, so '{request.SessionId}' takes it for {request.Tuning}.",
                Clearing: ClearedOnce(entry.Quiet)
            );
        }

        return null;
    }

    private PoolGrant? TakeATunerFromSomethingLessImportant(PoolRequest request, List<Action> after)
    {
        Entry? loser = Usable(request)
            .Where(entry => entry.IsOccupied && entry.Current!.Priority < request.Priority)
            .OrderBy(entry => entry.Current!.Priority)
            .FirstOrDefault();

        if (loser?.Current is not { } taken || !Move(loser, TunerAllocationEvent.TakeOver))
        {
            return null;
        }

        SessionId[] displaced = [.. taken.Sinks.Select(sink => sink.SessionId)];
        string names = string.Join("', '", displaced.Select(session => session.Value));

        if (loser.Previous is null)
        {
            loser.Previous = taken;
            taken.Ready.TrySetResult();
        }
        else
        {
            Evict(taken, TakenFrom(loser, taken.Opener));

            if (loser.Handover is { } claimed)
            {
                after.Add(() => claimed.TrySetResult(false));
            }
        }

        var handover = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loser.Handover = handover;
        loser.HandoverSince = timeProvider.GetUtcNow();
        loser.Current = new Occupancy(request.Tuning, request.SessionId);
        Attach(loser, loser.Current, request);
        SettleTheHandover(loser, after);

        return new PoolGrant(
            PoolVerdict.Granted,
            loser.DeviceId,
            request.SessionId,
            NeedsTuning: true,
            displaced,
            $"The tuner '{loser.DeviceId}' goes to '{request.SessionId}' for {request.Purpose.ToString().ToLowerInvariant()} on {request.Tuning}, which outranks '{names}'.",
            taken.Holder,
            handover.Task
        );
    }

    private PoolGrant TurnAway(PoolRequest request)
    {
        if (request.DeviceId is { } named)
        {
            return PoolGrant.Refused(
                PoolVerdict.DeviceBusy,
                entries.TryGetValue(named, out Entry? entry) && (entry.Current is not null || HoldsBack(entry, request))
                    ? WhyNot(entry, request)
                    : $"The tuner '{named}' cannot be had for {request.Tuning}."
            );
        }

        Entry[] considered = [.. request.Candidates.Select(EntryOf).Where(entry => entry.Enabled && entry.Fault is null)];
        string[] reasons = [.. considered.Select(entry => WhyNot(entry, request))];

        return PoolGrant.Refused(
            considered.Any(entry => entry.State is TunerAllocationState.Absent)
                ? PoolVerdict.DeviceUnavailable
                : PoolVerdict.NoDeviceFree,
            reasons.Length is 0
                ? $"No tuner can serve {request.Tuning}."
                : string.Join(" ", reasons)
        );
    }

    private static bool HoldsBack(Entry entry, PoolRequest request) =>
        entry.HeldBack is not null && entry.HeldBackFor == request.Tuning;

    private static string WhyNot(Entry entry, PoolRequest request)
    {
        if (HoldsBack(entry, request) && entry.HeldBack is { } failure)
        {
            return $"The tuner '{entry.DeviceId}' is held back after it could not be tuned: {failure.Message}";
        }

        if (entry.State is TunerAllocationState.Absent)
        {
            return $"The tuner '{entry.DeviceId}' is not plugged in.";
        }

        return entry.Current is { } occupancy
            ? $"The tuner '{entry.DeviceId}' is on {occupancy.Tuning} for '{string.Join("', '", occupancy.Sinks.Select(sink => sink.SessionId.Value))}', which '{request.SessionId}' does not outrank."
            : $"The tuner '{entry.DeviceId}' cannot be had.";
    }

    private static TunerRefusal TakenFrom(Entry entry, SessionId opener) =>
        new(SessionRefusal.DeviceBusy, $"The device '{entry.DeviceId}' was taken from '{opener}' while it was being tuned.");

    private void Attach(Entry entry, Occupancy occupancy, PoolRequest request)
    {
        turnedAway.Remove(request.SessionId);
        entry.IdleSince = null;
        occupancy.Sinks.Add(new Sink(request.SessionId, request.Priority));
        bySink[request.SessionId] = entry;
    }

    public bool Tuned(string deviceId, SessionId tuner, ITunerDevice device)
    {
        bool taken = false;

        lock (gate)
        {
            if (
                !disposed
                && entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.Tuning
                && entry.Current is { Device: null, Opening: null } occupancy
                && occupancy.Has(tuner)
                && Move(entry, TunerAllocationEvent.Opened)
            )
            {
                occupancy.Device = device;
                taken = true;
            }
        }

        Report();

        if (taken)
        {
            return true;
        }

        Close(device);
        LeavePrevious(deviceId, tuner);

        return false;
    }

    public void Ready(string deviceId, SessionId holder)
    {
        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.Current is { } occupancy
                && occupancy.Has(holder)
                && Move(entry, TunerAllocationEvent.Established)
            )
            {
                occupancy.Established = true;
                occupancy.Ready.TrySetResult();
            }
        }

        Report();
    }

    public void TuningFailed(string deviceId, SessionId tuner, Exception cause)
    {
        List<Action> after = [];

        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.Tuning
                && entry.Current is { Opening: null } occupancy
                && occupancy.Has(tuner)
            )
            {
                FailTheOpening(entry, occupancy, cause, null, after);
            }
            else
            {
                LeaveUnderGate(tuner, after);
            }
        }

        Run(after);
    }

    public TunerClearance? Clearance(string deviceId, SessionId opener)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(deviceId, out Entry? entry) || entry.Current is not { } occupancy || occupancy.Opener != opener || !occupancy.Has(opener))
            {
                return null;
            }

            return entry.State is TunerAllocationState.HandingOver && entry.Handover is { } handover
                ? new TunerClearance(handover.Task, entry.HandoverSince)
                : new TunerClearance(ClearedOnce(entry.Quiet), timeProvider.GetUtcNow());
        }
    }

    public OpenTicket? StartOpening(string deviceId, SessionId opener)
    {
        lock (gate)
        {
            if (
                disposed
                || !entries.TryGetValue(deviceId, out Entry? entry)
                || entry.State is not TunerAllocationState.Tuning
                || entry.Current is not { } occupancy
                || occupancy.Opener != opener
                || !occupancy.Has(opener)
            )
            {
                return null;
            }

            if (occupancy.Device is not null)
            {
                return new OpenTicket(deviceId, occupancy, Task.FromResult<TunerRefusal?>(null), Starts: false, Tunes: false);
            }

            if (occupancy.Opening is { } underWay)
            {
                return new OpenTicket(deviceId, occupancy, underWay.Task, Starts: false, Tunes: true);
            }

            var opening = new TaskCompletionSource<TunerRefusal?>(TaskCreationOptions.RunContinuationsAsynchronously);
            occupancy.Opening = opening;
            occupancy.InFlight = true;
            entry.InFlight++;
            entry.Quiet = Task.WhenAll(entry.Quiet, opening.Task);

            return new OpenTicket(deviceId, occupancy, opening.Task, Starts: true, Tunes: true);
        }
    }

    public void Opened(OpenTicket ticket, ITunerDevice device)
    {
        List<Action> after = [];
        var occupancy = (Occupancy)ticket.Token;
        TunerRefusal? outcome = null;

        lock (gate)
        {
            Entry entry = EntryOf(ticket.DeviceId);

            Landed(entry, occupancy);

            if (
                !disposed
                && ReferenceEquals(entry.Current, occupancy)
                && entry.State is TunerAllocationState.Tuning
                && occupancy.Device is null
                && Move(entry, TunerAllocationEvent.Opened)
            )
            {
                occupancy.Device = device;

                if (occupancy.Sinks.Count is 0)
                {
                    AfterSomeoneLeft(entry, occupancy, after, closing: false);
                }
            }
            else
            {
                outcome = TakenFrom(entry, occupancy.Opener);
                Drop(entry, device, after);

                if (ReferenceEquals(entry.Previous, occupancy))
                {
                    SettleTheHandover(entry, after);
                }
            }

            TaskCompletionSource<TunerRefusal?>? opening = occupancy.Opening;
            after.Add(() => opening?.TrySetResult(outcome));
        }

        Run(after);
    }

    public void OpenFailed(OpenTicket ticket, TunerRefusal refusal, Exception cause, Action? beforeAnswering = null)
    {
        List<Action> after = [];
        var occupancy = (Occupancy)ticket.Token;

        lock (gate)
        {
            Entry entry = EntryOf(ticket.DeviceId);

            Landed(entry, occupancy);

            if (ReferenceEquals(entry.Current, occupancy) && entry.State is TunerAllocationState.Tuning)
            {
                FailTheOpening(entry, occupancy, cause, refusal, after);
            }
            else if (ReferenceEquals(entry.Previous, occupancy))
            {
                SettleTheHandover(entry, after);
            }

            TaskCompletionSource<TunerRefusal?>? opening = occupancy.Opening;

            if (beforeAnswering is not null)
            {
                after.Add(beforeAnswering);
            }

            after.Add(() => opening?.TrySetResult(refusal));
        }

        Run(after);
    }

    private static void Landed(Entry entry, Occupancy occupancy)
    {
        if (occupancy.InFlight)
        {
            occupancy.InFlight = false;
            entry.InFlight--;
        }
    }

    private void FailTheOpening(Entry entry, Occupancy occupancy, Exception cause, TunerRefusal? refusal, List<Action> after)
    {
        if (!Move(entry, entry.Enabled ? TunerAllocationEvent.TuneFailed : TunerAllocationEvent.TuneFailedDisabled))
        {
            return;
        }

        Evict(occupancy, refusal);
        Drop(entry, occupancy.Device, after);
        entry.Current = null;

        if (entry.Enabled)
        {
            entry.HeldBack = cause;
            entry.HeldBackFor = occupancy.Tuning;
            entry.HeldBackUntil = timeProvider.GetUtcNow() + linger;
            LetLinger();
        }
        else
        {
            LetGoOf(entry, after);
        }
    }

    public TunerRefusal? RefusalOf(SessionId sessionId)
    {
        lock (gate)
        {
            return turnedAway.Remove(sessionId, out TunerRefusal? refusal) ? refusal : null;
        }
    }

    public async Task<TunerReadiness> AwaitReadyAsync(
        string deviceId,
        TimeSpan limit,
        SessionId reader = default,
        CancellationToken cancellationToken = default
    )
    {
        DateTimeOffset giveUpAt = timeProvider.GetUtcNow() + limit;

        while (true)
        {
            Task waiting;

            lock (gate)
            {
                if (Readiness(deviceId, reader, out Task? next) is { } settled)
                {
                    return settled;
                }

                waiting = next!;
            }

            TimeSpan left = giveUpAt - timeProvider.GetUtcNow();

            try
            {
                await waiting.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, timeProvider, cancellationToken);
            }
            catch (TimeoutException)
            {
                lock (gate)
                {
                    return Readiness(deviceId, reader, out _) is { Refusal: not null } refused
                        ? refused
                        : new TunerReadiness(false, null);
                }
            }
        }
    }

    private TunerReadiness? Readiness(string deviceId, SessionId reader, out Task? next)
    {
        next = null;

        if (!entries.TryGetValue(deviceId, out Entry? entry) || entry.Current is not { } occupancy)
        {
            return new TunerReadiness(false, Turned(reader));
        }

        if (!reader.IsUnset && !occupancy.Has(reader))
        {
            return new TunerReadiness(false, Turned(reader));
        }

        if (occupancy.Established)
        {
            return new TunerReadiness(IsEstablishedFor(entry, reader), null);
        }

        if (!reader.IsUnset && occupancy.Opener == reader)
        {
            return new TunerReadiness(false, null, Leads: true);
        }

        next = Task.WhenAny(occupancy.Ready.Task, occupancy.Stirred.Task);

        return null;
    }

    private TunerRefusal? Turned(SessionId reader) =>
        !reader.IsUnset && turnedAway.Remove(reader, out TunerRefusal? refusal) ? refusal : null;

    private static bool IsEstablishedFor(Entry entry, SessionId reader) =>
        entry.State is TunerAllocationState.InUse
        && entry.Current is { Established: true } occupancy
        && (reader.IsUnset || occupancy.Has(reader));

    private static TaskCompletionSource Unset() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsQuiet(string deviceId)
    {
        lock (gate)
        {
            return !entries.TryGetValue(deviceId, out Entry? entry)
                || (entry.Closing is 0 && entry.InFlight is 0);
        }
    }

    public bool Confirm(string deviceId, SessionId sessionId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.Tuning or TunerAllocationState.InUse
                && entry.Current is { } occupancy
                && occupancy.Has(sessionId);
        }
    }

    public ITunerDevice? DeviceOf(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry) ? entry.Current?.Device : null;
        }
    }

    public bool GiveBack(string deviceId, SessionId claimant, TunerRefusal? why = null)
    {
        List<Action> after = [];
        bool gaveBack = false;

        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.Current is { Established: false } occupancy
                && occupancy.Opener == claimant
                && occupancy.Has(claimant)
            )
            {
                gaveBack = GiveUpTheOpening(entry, occupancy, why, after);
            }
            else
            {
                LeaveUnderGate(claimant, after);
            }
        }

        Run(after);

        return gaveBack;
    }

    private bool GiveUpTheOpening(Entry entry, Occupancy occupancy, TunerRefusal? why, List<Action> after)
    {
        if (entry.State is TunerAllocationState.HandingOver)
        {
            return GiveBackUnderGate(entry, after, why);
        }

        Evict(occupancy, why);
        AfterSomeoneLeft(entry, occupancy, after, closing: true);

        return true;
    }

    private bool GiveBackUnderGate(Entry entry, List<Action> after, TunerRefusal? why)
    {
        Occupancy previous = entry.Previous!;
        TunerAllocationEvent back = previous.Established
            ? TunerAllocationEvent.GaveBack
            : TunerAllocationEvent.GaveBackTuning;

        if (!Move(entry, back))
        {
            return false;
        }

        Evict(entry.Current!, why);

        if (entry.Handover is { } handover)
        {
            after.Add(() => handover.TrySetResult(false));
        }

        if (!previous.Established)
        {
            previous.Ready = Unset();
        }

        entry.Current = previous;
        entry.Previous = null;
        entry.Handover = null;

        return true;
    }

    public void Abandon(string deviceId, SessionId sessionId)
    {
        List<Action> after = [];

        lock (gate)
        {
            LeaveUnderGate(sessionId, after, closing: true);
        }

        Run(after);
    }

    public void Leave(SessionId sessionId)
    {
        List<Action> after = [];

        lock (gate)
        {
            LeaveUnderGate(sessionId, after);
        }

        Run(after);
    }

    private void LeaveUnderGate(SessionId sessionId, List<Action> after, bool closing = false)
    {
        turnedAway.Remove(sessionId);

        if (!bySink.Remove(sessionId, out Entry? entry))
        {
            return;
        }

        if (entry.Current is { } current && current.Has(sessionId))
        {
            LeaveOccupancy(entry, current, sessionId);
            AfterSomeoneLeft(entry, current, after, closing);

            return;
        }

        if (entry.Previous is { } previous && previous.Has(sessionId))
        {
            LeaveOccupancy(entry, previous, sessionId);
            SettleTheHandover(entry, after);
        }
    }

    private void LeavePrevious(string deviceId, SessionId tuner)
    {
        List<Action> after = [];

        lock (gate)
        {
            if (
                bySink.TryGetValue(tuner, out Entry? attached)
                && attached.DeviceId == deviceId
                && attached.Previous is { } previous
                && previous.Has(tuner)
            )
            {
                LeaveUnderGate(tuner, after);
            }
        }

        Run(after);
    }

    private void LeaveOccupancy(Entry entry, Occupancy occupancy, SessionId sessionId)
    {
        occupancy.Sinks.RemoveAll(sink => sink.SessionId == sessionId);

        if (occupancy.Opener == sessionId && !occupancy.Established)
        {
            HandTheOpeningOn(entry, occupancy);

            return;
        }

        if (occupancy.Holder != sessionId)
        {
            return;
        }

        if (TheOneItWasTakenFrom(occupancy) is { } previous)
        {
            occupancy.Holder = previous;
        }
        else
        {
            Evict(occupancy);
        }

        occupancy.Displacing = null;
    }

    private void HandTheOpeningOn(Entry entry, Occupancy occupancy)
    {
        occupancy.Displacing = null;

        if (!ReferenceEquals(entry.Current, occupancy))
        {
            Evict(occupancy, TakenFrom(entry, occupancy.Opener));

            return;
        }

        if (occupancy.Sinks.Count is 0 || !Move(entry, TunerAllocationEvent.Promoted))
        {
            return;
        }

        Sink next = occupancy.Sinks.OrderByDescending(sink => sink.Priority).First();

        occupancy.Opener = next.SessionId;
        occupancy.Holder = next.SessionId;
        occupancy.Stirred.TrySetResult();
        occupancy.Stirred = Unset();
    }

    private void AfterSomeoneLeft(Entry entry, Occupancy occupancy, List<Action> after, bool closing)
    {
        if (occupancy.Sinks.Count > 0 || occupancy.InFlight)
        {
            Move(entry, TunerAllocationEvent.Released);

            return;
        }

        if (entry.State is TunerAllocationState.HandingOver)
        {
            GiveBackUnderGate(entry, after, null);

            return;
        }

        occupancy.Ready.TrySetResult();

        if (!entry.Enabled)
        {
            if (Move(entry, TunerAllocationEvent.EmptiedDisabled))
            {
                Drop(entry, occupancy.Device, after);
                entry.Current = null;
                LetGoOf(entry, after);
            }

            return;
        }

        if (occupancy.Device is not null && !closing)
        {
            if (Move(entry, TunerAllocationEvent.Emptied))
            {
                entry.IdleSince = timeProvider.GetUtcNow();
                LetLinger();
            }

            return;
        }

        if (Move(entry, TunerAllocationEvent.EmptiedClosed))
        {
            Drop(entry, occupancy.Device, after);
            entry.Current = null;
        }
    }

    private void SettleTheHandover(Entry entry, List<Action> after)
    {
        if (
            entry.State is not TunerAllocationState.HandingOver
            || entry.Previous is not { } previous
            || previous.Sinks.Count > 0
            || previous.InFlight
            || !Move(entry, TunerAllocationEvent.PreviousGone)
        )
        {
            return;
        }

        Drop(entry, previous.Device, after);
        entry.Previous = null;

        if (entry.Handover is { } handover)
        {
            Task quiet = entry.Quiet;
            after.Add(() => quiet.ContinueWith(
                _ => handover.TrySetResult(true),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            ));
        }

        entry.Handover = null;
    }

    public IReadOnlyList<SessionId> WhoElseIsReadingThrough(SessionId holder)
    {
        lock (gate)
        {
            if (
                !bySink.TryGetValue(holder, out Entry? entry)
                || entry.Current is not { } occupancy
                || occupancy.Holder != holder
                || occupancy.Displacing is not null
            )
            {
                return [];
            }

            return
            [
                .. occupancy
                    .Sinks.Where(sink => sink.SessionId != holder)
                    .OrderByDescending(sink => sink.Priority)
                    .Select(sink => sink.SessionId),
            ];
        }
    }

    public bool HandTheTunerOn(SessionId holder, SessionId next)
    {
        bool handed = HandTheTunerOnUnderGate(holder, next);

        Report();

        return handed;
    }

    private bool HandTheTunerOnUnderGate(SessionId holder, SessionId next)
    {
        lock (gate)
        {
            if (
                !bySink.TryGetValue(holder, out Entry? entry)
                || entry.State is not TunerAllocationState.InUse
                || entry.Current is not { } occupancy
                || occupancy.Holder != holder
                || occupancy.Displacing is not null
                || !occupancy.Has(next)
                || !Move(entry, TunerAllocationEvent.PassOn)
            )
            {
                return false;
            }

            bySink.Remove(holder);
            occupancy.Sinks.RemoveAll(sink => sink.SessionId == holder);
            occupancy.Holder = next;
            occupancy.Displacing = null;

            return true;
        }
    }

    public void SeatTaken(string deviceId, SessionId holder)
    {
        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.Current is { } occupancy
                && occupancy.Holder == holder
                && occupancy.Displacing is not null
                && Move(entry, TunerAllocationEvent.SeatSettled)
            )
            {
                occupancy.Displacing = null;
            }
        }

        Report();
    }

    private static SessionId? TheOneItWasTakenFrom(Occupancy occupancy) =>
        occupancy.Displacing is { } previous && occupancy.Has(previous) ? previous : null;

    private void Evict(Occupancy occupancy, TunerRefusal? why = null)
    {
        foreach (Sink sink in occupancy.Sinks)
        {
            if (bySink.TryGetValue(sink.SessionId, out Entry? entry) && Holds(entry, occupancy))
            {
                bySink.Remove(sink.SessionId);

                if (why is not null)
                {
                    turnedAway[sink.SessionId] = why;
                }
            }
        }

        occupancy.Sinks.Clear();
        occupancy.Ready.TrySetResult();
        occupancy.Stirred.TrySetResult();
    }

    private static bool Holds(Entry entry, Occupancy occupancy) =>
        ReferenceEquals(entry.Current, occupancy) || ReferenceEquals(entry.Previous, occupancy);

    public void Sweep()
    {
        List<Action> after = [];

        lock (gate)
        {
            Expire(after);
        }

        Run(after);
    }

    public void Fault(string deviceId, TunerFault fault)
    {
        List<Action> after = [];

        lock (gate)
        {
            Entry entry = EntryOf(deviceId);
            TunerAllocationEvent faulted = entry.IsOccupied && !entry.Enabled
                ? TunerAllocationEvent.FaultedDisabled
                : TunerAllocationEvent.Faulted;

            if (Move(entry, faulted))
            {
                entry.Fault = fault;
                Clear(entry, after);
                LetGoOf(entry, after);
            }
        }

        Run(after);
    }

    public void Restore(string deviceId)
    {
        List<Action> after = [];

        lock (gate)
        {
            Entry entry = EntryOf(deviceId);
            TunerAllocationEvent restored = entry.State is TunerAllocationState.Faulted && !entry.Present
                ? TunerAllocationEvent.RestoredAbsent
                : TunerAllocationEvent.Restored;

            if (entry.Fault is not null && Move(entry, restored))
            {
                entry.Fault = null;
            }
        }

        Run(after);
    }

    public void Turn(string deviceId, bool enabled)
    {
        List<Action> after = [];

        lock (gate)
        {
            Entry entry = EntryOf(deviceId);

            if (enabled)
            {
                Enable(entry);
            }
            else
            {
                Disable(entry, after);
            }
        }

        Run(after);
    }

    private void Enable(Entry entry)
    {
        if (entry.Enabled)
        {
            return;
        }

        TunerAllocationEvent enabled = entry switch
        {
            { State: not TunerAllocationState.Disabled } => TunerAllocationEvent.Enabled,
            { Fault: not null } => TunerAllocationEvent.EnabledFaulted,
            { Present: false } => TunerAllocationEvent.EnabledAbsent,
            _ => TunerAllocationEvent.Enabled,
        };

        if (Move(entry, enabled))
        {
            entry.Enabled = true;
        }
    }

    private void Disable(Entry entry, List<Action> after)
    {
        if (!entry.Enabled)
        {
            LetGoOf(entry, after);

            return;
        }

        bool occupied = entry.IsOccupied;

        if (!Move(entry, TunerAllocationEvent.Disabled))
        {
            return;
        }

        entry.Enabled = false;

        if (!occupied)
        {
            Drop(entry, entry.Current?.Device, after);
            entry.Current = null;
            entry.HeldBack = null;
        }

        LetGoOf(entry, after);
    }

    public void Looked(string deviceId, bool present)
    {
        List<Action> after = [];

        lock (gate)
        {
            Entry entry = EntryOf(deviceId);

            if (!entry.IsOccupied)
            {
                See(entry, present, after);
            }
        }

        Run(after);
    }

    private void See(Entry entry, bool present, List<Action> after)
    {
        entry.Present = present;

        if (present && entry.State is TunerAllocationState.Absent)
        {
            Move(entry, TunerAllocationEvent.PluggedIn);

            return;
        }

        if (present || entry.State is not (TunerAllocationState.Free or TunerAllocationState.Ready))
        {
            return;
        }

        if (Move(entry, TunerAllocationEvent.Unplugged))
        {
            Drop(entry, entry.Current?.Device, after);
            entry.Current = null;
            entry.HeldBack = null;
            LetGoOf(entry, after);
        }
    }

    private void Clear(Entry entry, List<Action> after)
    {
        foreach (Occupancy occupancy in new[] { entry.Current, entry.Previous }.OfType<Occupancy>())
        {
            Evict(occupancy);
            Drop(entry, occupancy.Device, after);
        }

        if (entry.Handover is { } handover)
        {
            after.Add(() => handover.TrySetResult(false));
        }

        entry.Current = null;
        entry.Previous = null;
        entry.Handover = null;
        entry.HeldBack = null;
        entry.IdleSince = null;
    }

    private void LetGoOf(Entry entry, List<Action> after)
    {
        if (letGo is { } release)
        {
            string deviceId = entry.DeviceId;
            after.Add(() => release(deviceId));
        }
    }

    public bool IsHeld(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry)
                && entry.IsOccupied
                && entry.Current is { Sinks.Count: > 0 };
        }
    }

    public bool IsLingering(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry)
                && (entry.State is TunerAllocationState.Ready || entry.HeldBack is not null);
        }
    }

    public IReadOnlyList<SessionId> SinksOn(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry) && entry.Current is { } occupancy
                ? [.. occupancy.Sinks.Select(sink => sink.SessionId)]
                : [];
        }
    }

    public IReadOnlyList<string> Occupied()
    {
        lock (gate)
        {
            return [.. entries.Values.Where(entry => entry.IsOccupied).Select(entry => entry.DeviceId)];
        }
    }

    public bool? IsEnabled(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry) ? entry.Enabled : null;
        }
    }

    public TunerFault? FaultOf(string deviceId)
    {
        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry) ? entry.Fault : null;
        }
    }

    public TunerAllocationState StateOf(string deviceId)
    {
        lock (gate)
        {
            return EntryOf(deviceId).State;
        }
    }

    public IReadOnlyList<TunerAllocationView> Snapshot()
    {
        lock (gate)
        {
            return [.. entries.Values.Select(View)];
        }
    }

    private static TunerAllocationView View(Entry entry) =>
        new(
            entry.DeviceId,
            entry.State,
            entry.Current?.Tuning,
            entry.Current?.Holder ?? default,
            [.. (entry.Current?.Sinks ?? []).Select(sink => sink.SessionId)],
            [.. (entry.Previous?.Sinks ?? []).Select(sink => sink.SessionId)],
            entry.Current?.Device is not null,
            entry.Previous?.Device is not null,
            entry.Current?.InFlight ?? false,
            entry.Previous?.InFlight ?? false,
            entry.Enabled,
            entry.Fault is not null
        );

    public void Dispose()
    {
        List<Action> after = [];

        lock (gate)
        {
            disposed = true;
            sweeper?.Dispose();
            sweeper = null;

            foreach (Entry entry in entries.Values)
            {
                foreach (Occupancy occupancy in new[] { entry.Current, entry.Previous }.OfType<Occupancy>())
                {
                    occupancy.Ready.TrySetResult();
                    Drop(entry, occupancy.Device, after);
                    occupancy.Device = null;
                }

                if (entry.Handover is { } handover)
                {
                    after.Add(() => handover.TrySetResult(false));
                }
            }

            bySink.Clear();
        }

        Run(after);
    }

    private void LetLinger()
    {
        if (disposed)
        {
            return;
        }

        sweeper ??= timeProvider.CreateTimer(
            static pool => ((TunerPool)pool!).Sweep(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        sweeper.Change(linger + TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
    }

    private void Expire(List<Action> after)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (Entry entry in entries.Values)
        {
            if (entry.HeldBack is not null && entry.HeldBackUntil <= now)
            {
                entry.HeldBack = null;
            }

            if (
                entry.State is not TunerAllocationState.Ready
                || entry.IdleSince is not { } since
                || now - since < linger
                || !Move(entry, TunerAllocationEvent.Expired)
            )
            {
                continue;
            }

            Drop(entry, entry.Current?.Device, after);
            entry.Current = null;
            entry.IdleSince = null;
        }
    }

    private Entry EntryOf(string deviceId)
    {
        if (!entries.TryGetValue(deviceId, out Entry? entry))
        {
            entry = new Entry(deviceId, enabled: true);
            entries[deviceId] = entry;
        }

        return entry;
    }

    private static Task<bool> ClearedOnce(Task quiet) =>
        quiet.IsCompleted
            ? Task.FromResult(true)
            : quiet.ContinueWith(
                static _ => true,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

    private void Drop(Entry entry, ITunerDevice? device, List<Action> after)
    {
        if (device is null)
        {
            return;
        }

        TaskCompletionSource closed = Unset();

        entry.Quiet = entry.Quiet.IsCompleted ? closed.Task : Task.WhenAll(entry.Quiet, closed.Task);
        entry.Closing++;
        after.Add(() =>
        {
            Close(device);

            lock (gate)
            {
                entry.Closing--;
            }

            closed.TrySetResult();
        });
    }

    private bool Move(Entry entry, TunerAllocationEvent on)
    {
        if (TunerTransitions.TryMove(entry.State, on, out TunerAllocationState next))
        {
            entry.State = next;

            return true;
        }

        refusals++;
        refusedTransitions.Add($"The tuner '{entry.DeviceId}' is {entry.State} and cannot take {on}; the change was refused.");

        return false;
    }

    private void Run(List<Action> after)
    {
        Report();

        foreach (Action action in after)
        {
            action();
        }
    }

    private void Report()
    {
        string[] refused;

        lock (gate)
        {
            refused = [.. refusedTransitions];
            refusedTransitions.Clear();
        }

        foreach (string message in refused)
        {
            logger?.LogError("{Refused}", message);
        }
    }

    private static void Close(ITunerDevice device)
    {
        try
        {
            device.Dispose();
        }
        catch (Exception)
        {
            return;
        }
    }
}
