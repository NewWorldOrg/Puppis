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
    bool Enabled,
    bool Faulted
);

public sealed class TunerPool(
    TimeProvider timeProvider,
    TimeSpan? grace = null,
    IEnumerable<(string DeviceId, bool Enabled)>? declared = null,
    Action<string>? letGo = null,
    ILogger? logger = null
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

        public TaskCompletionSource Ready { get; set; } = Unset();

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

        public DateTimeOffset HeldBackUntil { get; set; }

        public Task Quiet { get; set; } = Task.CompletedTask;

        public Dictionary<SessionId, TaskCompletionSource> Stragglers { get; } = [];

        public bool IsOccupied =>
            State is TunerAllocationState.Tuning
                or TunerAllocationState.InUse
                or TunerAllocationState.HandingOver;
    }

    private readonly Dictionary<string, Entry> entries = Declare(declared);
    private readonly Dictionary<SessionId, Entry> bySink = [];
    private readonly Dictionary<SessionId, Entry> straggling = [];
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
        List<Action> after = [];
        PoolGrant grant;

        lock (gate)
        {
            Expire(after);

            grant =
                Ineligible(request)
                ?? RideAlong(request, after)
                ?? TakeAFreeTuner(request)
                ?? TakeATunerNobodyIsOn(request, after)
                ?? TakeATunerFromSomethingLessImportant(request, after)
                ?? TurnAway(request);
        }

        Run(after);

        return grant;
    }

    private PoolGrant? Ineligible(PoolRequest request)
    {
        if (disposed)
        {
            return PoolGrant.Refused(PoolVerdict.NoDeviceFree, "The driver is shutting down, so no tuner is handed out.");
        }

        if (request.DeviceId is { } named)
        {
            return IneligibleNamed(EntryOf(named), request);
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

    private static PoolGrant? IneligibleNamed(Entry entry, PoolRequest request)
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

        return entry.State is TunerAllocationState.Absent
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
        occupancy.Ready = Unset();
        Attach(entry, occupancy, request);

        return new PoolGrant(
            PoolVerdict.Granted,
            entry.DeviceId,
            request.SessionId,
            NeedsTuning: false,
            [],
            $"The tuner '{entry.DeviceId}' was still on {request.Tuning}, so '{request.SessionId}' takes it back without tuning it again.",
            Clearing: ClearedOnce(QuietOf(entry))
        );
    }

    private PoolGrant? TakeAFreeTuner(PoolRequest request)
    {
        foreach (Entry entry in Usable(request))
        {
            if (entry.State is not TunerAllocationState.Free || entry.HeldBack is not null)
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
                Clearing: ClearedOnce(QuietOf(entry))
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
                Clearing: ClearedOnce(QuietOf(entry))
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
            WakeEveryoneWaitingOn(taken);
        }
        else
        {
            Evict(taken);

            if (loser.Handover is { } claimed)
            {
                after.Add(() => claimed.TrySetResult(false));
            }
        }

        var handover = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loser.Handover = handover;
        loser.Current = new Occupancy(request.Tuning, request.SessionId);
        Attach(loser, loser.Current, request);

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
                entries.TryGetValue(named, out Entry? entry) && (entry.Current is not null || entry.HeldBack is not null)
                    ? WhyNot(entry, request)
                    : $"The tuner '{named}' cannot be had for {request.Tuning}."
            );
        }

        string[] reasons =
        [
            .. request
                .Candidates.Select(EntryOf)
                .Where(entry => entry.Enabled && entry.Fault is null)
                .Select(entry => WhyNot(entry, request)),
        ];

        return PoolGrant.Refused(
            PoolVerdict.NoDeviceFree,
            reasons.Length is 0
                ? $"No tuner can serve {request.Tuning}."
                : string.Join(" ", reasons)
        );
    }

    private static string WhyNot(Entry entry, PoolRequest request)
    {
        if (entry.HeldBack is { } failure)
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

    private void Attach(Entry entry, Occupancy occupancy, PoolRequest request)
    {
        entry.IdleSince = null;
        occupancy.Sinks.Add(new Sink(request.SessionId, request.Priority));
        bySink[request.SessionId] = entry;
    }

    public bool Tuned(string deviceId, SessionId tuner, ITunerDevice device)
    {
        List<Action> after = [];

        lock (gate)
        {
            if (
                !disposed
                && entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.Tuning
                && entry.Current is { Device: null } occupancy
                && occupancy.Has(tuner)
                && Move(entry, TunerAllocationEvent.Opened)
            )
            {
                occupancy.Device = device;

                return true;
            }
        }

        Close(device);
        Gone(deviceId, tuner);

        return false;
    }

    public void Ready(string deviceId, SessionId holder)
    {
        List<Action> after = [];

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

        Run(after);
    }

    public void TuningFailed(string deviceId, SessionId tuner, Exception cause)
    {
        List<Action> after = [];

        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.Tuning
                && entry.Current is { } occupancy
                && occupancy.Has(tuner)
                && Move(entry, entry.Enabled ? TunerAllocationEvent.TuneFailed : TunerAllocationEvent.TuneFailedDisabled)
            )
            {
                Evict(occupancy);
                Drop(entry, occupancy.Device, after);
                entry.Current = null;

                if (entry.Enabled)
                {
                    entry.HeldBack = cause;
                    entry.HeldBackUntil = timeProvider.GetUtcNow() + linger;
                    LetLinger();
                }
                else
                {
                    LetGoOf(entry, after);
                }
            }
            else
            {
                LeaveUnderGate(tuner, after);
            }
        }

        Run(after);
    }

    public async Task<bool> AwaitReadyAsync(string deviceId, TimeSpan limit, SessionId reader = default)
    {
        Task ready;

        lock (gate)
        {
            if (!entries.TryGetValue(deviceId, out Entry? entry) || entry.Current is not { } occupancy)
            {
                return false;
            }

            if (occupancy.Established)
            {
                return IsEstablishedFor(entry, reader);
            }

            ready = occupancy.Ready.Task;
        }

        try
        {
            await ready.WaitAsync(limit, timeProvider);
        }
        catch (TimeoutException)
        {
            return false;
        }

        lock (gate)
        {
            return entries.TryGetValue(deviceId, out Entry? entry) && IsEstablishedFor(entry, reader);
        }
    }

    private static bool IsEstablishedFor(Entry entry, SessionId reader) =>
        entry.State is TunerAllocationState.InUse
        && entry.Current is { Established: true } occupancy
        && (reader.IsUnset || occupancy.Has(reader));

    private static TaskCompletionSource Unset() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    public bool GiveBack(string deviceId, SessionId claimant)
    {
        List<Action> after = [];
        bool gaveBack = false;

        lock (gate)
        {
            if (
                entries.TryGetValue(deviceId, out Entry? entry)
                && entry.State is TunerAllocationState.HandingOver
                && entry.Current?.Holder == claimant
            )
            {
                gaveBack = GiveBackUnderGate(entry, after);
            }
            else
            {
                LeaveUnderGate(claimant, after);
            }
        }

        Run(after);

        return gaveBack;
    }

    private bool GiveBackUnderGate(Entry entry, List<Action> after)
    {
        Occupancy previous = entry.Previous!;
        TunerAllocationEvent back = previous.Established
            ? TunerAllocationEvent.GaveBack
            : TunerAllocationEvent.GaveBackTuning;

        if (!Move(entry, back))
        {
            return false;
        }

        Evict(entry.Current!);

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

    public void Leave(SessionId sessionId)
    {
        List<Action> after = [];

        lock (gate)
        {
            LeaveUnderGate(sessionId, after);
        }

        Run(after);
    }

    private void LeaveUnderGate(SessionId sessionId, List<Action> after)
    {
        if (straggling.Remove(sessionId, out Entry? strayedFrom))
        {
            Settle(strayedFrom, sessionId, after);
        }

        if (!bySink.Remove(sessionId, out Entry? entry))
        {
            return;
        }

        if (entry.Current is { } current && current.Has(sessionId))
        {
            LeaveOccupancy(current, sessionId);
            AfterSomeoneLeft(entry, current, after);

            return;
        }

        if (entry.Previous is { } previous && previous.Has(sessionId))
        {
            LeaveOccupancy(previous, sessionId);
            SettleTheHandover(entry, after);
        }
    }

    private void LeaveOccupancy(Occupancy occupancy, SessionId sessionId)
    {
        occupancy.Sinks.RemoveAll(sink => sink.SessionId == sessionId);

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

    private void AfterSomeoneLeft(Entry entry, Occupancy occupancy, List<Action> after)
    {
        if (occupancy.Sinks.Count > 0)
        {
            Move(entry, TunerAllocationEvent.Released);

            return;
        }

        if (entry.State is TunerAllocationState.HandingOver)
        {
            GiveBackUnderGate(entry, after);

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

        if (occupancy.Device is not null)
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
            entry.Current = null;
        }
    }

    private void SettleTheHandover(Entry entry, List<Action> after)
    {
        if (
            entry.State is not TunerAllocationState.HandingOver
            || entry.Previous is not { } previous
            || previous.Sinks.Count > 0
            || !Move(entry, TunerAllocationEvent.PreviousGone)
        )
        {
            return;
        }

        Drop(entry, previous.Device, after);
        entry.Previous = null;

        if (entry.Handover is { } handover)
        {
            Task quiet = QuietOf(entry);
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
        List<Action> after = [];

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
        List<Action> after = [];

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

        Run(after);
    }

    private static SessionId? TheOneItWasTakenFrom(Occupancy occupancy) =>
        occupancy.Displacing is { } previous && occupancy.Has(previous) ? previous : null;

    private void Evict(Occupancy occupancy)
    {
        foreach (Sink sink in occupancy.Sinks)
        {
            if (bySink.TryGetValue(sink.SessionId, out Entry? entry) && Holds(entry, occupancy))
            {
                bySink.Remove(sink.SessionId);
            }
        }

        occupancy.Sinks.Clear();
        occupancy.Ready.TrySetResult();
    }

    private static bool Holds(Entry entry, Occupancy occupancy) =>
        ReferenceEquals(entry.Current, occupancy) || ReferenceEquals(entry.Previous, occupancy);

    private static void WakeEveryoneWaitingOn(Occupancy occupancy) => occupancy.Ready.TrySetResult();

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
                Enable(entry, after);
            }
            else
            {
                Disable(entry, after);
            }
        }

        Run(after);
    }

    private void Enable(Entry entry, List<Action> after)
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
            if (!occupancy.Established && occupancy.Device is null && occupancy.Sinks.Count > 0)
            {
                Straggle(entry, occupancy.Opener);
            }

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

    private void Straggle(Entry entry, SessionId opener)
    {
        TaskCompletionSource done = Unset();

        entry.Stragglers[opener] = done;
        straggling[opener] = entry;
        entry.Quiet = Task.WhenAll(entry.Quiet, done.Task);
    }

    private void Gone(string deviceId, SessionId tuner)
    {
        List<Action> after = [];

        lock (gate)
        {
            if (straggling.Remove(tuner, out Entry? entry))
            {
                Settle(entry, tuner, after);
            }

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

    private static void Settle(Entry entry, SessionId sessionId, List<Action> after)
    {
        if (entry.Stragglers.Remove(sessionId, out TaskCompletionSource? done))
        {
            after.Add(() => done.TrySetResult());
        }
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

    private static Task QuietOf(Entry entry) => entry.Quiet;

    private static Task<bool> ClearedOnce(Task quiet) =>
        quiet.IsCompleted
            ? Task.FromResult(true)
            : quiet.ContinueWith(
                static _ => true,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

    private static void Drop(Entry entry, ITunerDevice? device, List<Action> after)
    {
        if (device is null)
        {
            return;
        }

        TaskCompletionSource closed = Unset();

        entry.Quiet = entry.Quiet.IsCompleted ? closed.Task : Task.WhenAll(entry.Quiet, closed.Task);
        after.Add(() =>
        {
            Close(device);
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

        foreach (Action action in after)
        {
            action();
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
