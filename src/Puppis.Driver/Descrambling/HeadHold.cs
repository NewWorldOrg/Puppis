using Puppis.Contracts;

namespace Puppis.Driver.Descrambling;

public enum HeadHoldKind
{
    EveryProgramme,

    WhatIsHeld,
}

public sealed record LiveService(ServiceTablesKey Key, ServiceTableMemory Memory, bool PutsTablesInFront = true);

public sealed record HeadHold(HeadHoldKind Kind, LiveService? Service = null)
{
    public static readonly HeadHold EveryProgramme = new(HeadHoldKind.EveryProgramme);

    public static readonly HeadHold WhatIsHeld = new(HeadHoldKind.WhatIsHeld);

    public HeadHold FromTheMiddle() => Service is null ? this : this with { Service = Service with { PutsTablesInFront = false } };
}

public static class HeadHolds
{
    public static HeadHold For(SessionPurpose purpose) =>
        purpose is SessionPurpose.Live ? HeadHold.WhatIsHeld : HeadHold.EveryProgramme;

    public static HeadHold For(SessionPurpose purpose, TuningRequest tuning, ServiceTableMemory memory)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(memory);

        if (purpose is not SessionPurpose.Live || tuning.ServiceId is not int service)
        {
            return For(purpose);
        }

        ServiceTablesKey key = new(tuning.Kind, tuning.PhysicalChannel, service);

        return new HeadHold(HeadHoldKind.WhatIsHeld, new LiveService(key, memory));
    }

    public static bool ReadsFromTheLock(HeadHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        return hold.Kind is HeadHoldKind.WhatIsHeld;
    }
}
