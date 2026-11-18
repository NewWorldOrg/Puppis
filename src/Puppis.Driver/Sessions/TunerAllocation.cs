using System.Collections.Frozen;

namespace Puppis.Driver.Sessions;

public enum TunerAllocationState
{
    Absent,
    Disabled,
    Faulted,
    Free,
    Ready,
    Tuning,
    InUse,
    HandingOver,
}

public enum TunerAllocationEvent
{
    Claim,
    Ride,
    TakeSeat,
    Promoted,
    SeatSettled,
    PassOn,
    TakeOver,
    Opened,
    Established,
    Released,
    Emptied,
    EmptiedClosed,
    EmptiedDisabled,
    TuneFailed,
    TuneFailedDisabled,
    PreviousGone,
    GaveBack,
    GaveBackTuning,
    Expired,
    Unplugged,
    PluggedIn,
    Disabled,
    Enabled,
    EnabledAbsent,
    EnabledFaulted,
    Faulted,
    FaultedDisabled,
    Restored,
    RestoredAbsent,
}

public static class TunerTransitions
{
    private const TunerAllocationState Absent = TunerAllocationState.Absent;
    private const TunerAllocationState Disabled = TunerAllocationState.Disabled;
    private const TunerAllocationState Faulted = TunerAllocationState.Faulted;
    private const TunerAllocationState Free = TunerAllocationState.Free;
    private const TunerAllocationState Ready = TunerAllocationState.Ready;
    private const TunerAllocationState Tuning = TunerAllocationState.Tuning;
    private const TunerAllocationState InUse = TunerAllocationState.InUse;
    private const TunerAllocationState HandingOver = TunerAllocationState.HandingOver;

    public static IReadOnlyList<(TunerAllocationState From, TunerAllocationEvent On, TunerAllocationState To)> All { get; } =
    [
        (Free, TunerAllocationEvent.Claim, Tuning),
        (Ready, TunerAllocationEvent.Claim, Tuning),

        (Tuning, TunerAllocationEvent.Ride, Tuning),
        (InUse, TunerAllocationEvent.Ride, InUse),
        (HandingOver, TunerAllocationEvent.Ride, HandingOver),

        (Tuning, TunerAllocationEvent.TakeSeat, Tuning),
        (InUse, TunerAllocationEvent.TakeSeat, InUse),
        (HandingOver, TunerAllocationEvent.TakeSeat, HandingOver),

        (Tuning, TunerAllocationEvent.Promoted, Tuning),
        (HandingOver, TunerAllocationEvent.Promoted, HandingOver),

        (Tuning, TunerAllocationEvent.SeatSettled, Tuning),
        (InUse, TunerAllocationEvent.SeatSettled, InUse),

        (InUse, TunerAllocationEvent.PassOn, InUse),

        (Tuning, TunerAllocationEvent.TakeOver, HandingOver),
        (InUse, TunerAllocationEvent.TakeOver, HandingOver),
        (HandingOver, TunerAllocationEvent.TakeOver, HandingOver),

        (Tuning, TunerAllocationEvent.Opened, Tuning),

        (Tuning, TunerAllocationEvent.Established, InUse),
        (InUse, TunerAllocationEvent.Established, InUse),

        (Tuning, TunerAllocationEvent.Released, Tuning),
        (InUse, TunerAllocationEvent.Released, InUse),
        (HandingOver, TunerAllocationEvent.Released, HandingOver),

        (Tuning, TunerAllocationEvent.Emptied, Ready),
        (InUse, TunerAllocationEvent.Emptied, Ready),
        (Tuning, TunerAllocationEvent.EmptiedClosed, Free),
        (InUse, TunerAllocationEvent.EmptiedClosed, Free),
        (Tuning, TunerAllocationEvent.EmptiedDisabled, Disabled),
        (InUse, TunerAllocationEvent.EmptiedDisabled, Disabled),

        (Tuning, TunerAllocationEvent.TuneFailed, Free),
        (Tuning, TunerAllocationEvent.TuneFailedDisabled, Disabled),

        (HandingOver, TunerAllocationEvent.PreviousGone, Tuning),
        (HandingOver, TunerAllocationEvent.GaveBack, InUse),
        (HandingOver, TunerAllocationEvent.GaveBackTuning, Tuning),

        (Ready, TunerAllocationEvent.Expired, Free),

        (Free, TunerAllocationEvent.Unplugged, Absent),
        (Ready, TunerAllocationEvent.Unplugged, Absent),
        (Absent, TunerAllocationEvent.PluggedIn, Free),

        (Free, TunerAllocationEvent.Disabled, Disabled),
        (Ready, TunerAllocationEvent.Disabled, Disabled),
        (Absent, TunerAllocationEvent.Disabled, Disabled),
        (Faulted, TunerAllocationEvent.Disabled, Disabled),
        (Tuning, TunerAllocationEvent.Disabled, Tuning),
        (InUse, TunerAllocationEvent.Disabled, InUse),
        (HandingOver, TunerAllocationEvent.Disabled, HandingOver),

        (Disabled, TunerAllocationEvent.Enabled, Free),
        (Disabled, TunerAllocationEvent.EnabledAbsent, Absent),
        (Disabled, TunerAllocationEvent.EnabledFaulted, Faulted),
        (Tuning, TunerAllocationEvent.Enabled, Tuning),
        (InUse, TunerAllocationEvent.Enabled, InUse),
        (HandingOver, TunerAllocationEvent.Enabled, HandingOver),

        (Free, TunerAllocationEvent.Faulted, Faulted),
        (Ready, TunerAllocationEvent.Faulted, Faulted),
        (Absent, TunerAllocationEvent.Faulted, Faulted),
        (Tuning, TunerAllocationEvent.Faulted, Faulted),
        (InUse, TunerAllocationEvent.Faulted, Faulted),
        (HandingOver, TunerAllocationEvent.Faulted, Faulted),
        (Faulted, TunerAllocationEvent.Faulted, Faulted),
        (Disabled, TunerAllocationEvent.Faulted, Disabled),
        (Tuning, TunerAllocationEvent.FaultedDisabled, Disabled),
        (InUse, TunerAllocationEvent.FaultedDisabled, Disabled),
        (HandingOver, TunerAllocationEvent.FaultedDisabled, Disabled),

        (Faulted, TunerAllocationEvent.Restored, Free),
        (Faulted, TunerAllocationEvent.RestoredAbsent, Absent),
        (Disabled, TunerAllocationEvent.Restored, Disabled),
    ];

    private static readonly FrozenDictionary<(TunerAllocationState, TunerAllocationEvent), TunerAllocationState> Next =
        All.ToFrozenDictionary(transition => (transition.From, transition.On), transition => transition.To);

    public static bool TryMove(
        TunerAllocationState from,
        TunerAllocationEvent on,
        out TunerAllocationState to
    ) => Next.TryGetValue((from, on), out to);
}
