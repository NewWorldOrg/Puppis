using Puppis.Contracts;

namespace Puppis.Driver.Descrambling;

public enum HeadHoldKind
{
    EveryProgramme,

    WhatIsHeld,
}

public sealed record LiveTables(TablesKey Key, TableMemory Memory, bool PutsTablesInFront = true);

public sealed record HeadHold(HeadHoldKind Kind, LiveTables? Tables = null)
{
    public static readonly HeadHold EveryProgramme = new(HeadHoldKind.EveryProgramme);

    public static readonly HeadHold WhatIsHeld = new(HeadHoldKind.WhatIsHeld);

    public HeadHold FromTheMiddle() => Tables is null ? this : this with { Tables = Tables with { PutsTablesInFront = false } };
}

public static class HeadHolds
{
    public static HeadHold For(SessionPurpose purpose) =>
        purpose is SessionPurpose.Live ? HeadHold.WhatIsHeld : HeadHold.EveryProgramme;

    public static HeadHold For(SessionPurpose purpose, TuningRequest tuning, TableMemory memory) =>
        For(purpose, tuning, null, memory);

    public static HeadHold For(SessionPurpose purpose, TuningRequest tuning, TuneParams? tune, TableMemory memory)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(memory);

        if (purpose is not SessionPurpose.Live)
        {
            return For(purpose);
        }

        TuningRequest tuned = tune?.ToLegacyRequest() ?? tuning;
        TablesKey key = new(tuned.Kind, tuned.PhysicalChannel, tuning.ServiceId, tune);

        return new HeadHold(HeadHoldKind.WhatIsHeld, new LiveTables(key, memory));
    }

    public static bool ReadsFromTheLock(HeadHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        return hold.Kind is HeadHoldKind.WhatIsHeld;
    }
}
