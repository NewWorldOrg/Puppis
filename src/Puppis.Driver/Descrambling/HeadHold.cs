using Puppis.Contracts;

namespace Puppis.Driver.Descrambling;

public enum HeadHold
{
    EveryProgramme,

    WhatIsHeld,
}

public static class HeadHolds
{
    public static HeadHold For(SessionPurpose purpose) =>
        purpose is SessionPurpose.Live ? HeadHold.WhatIsHeld : HeadHold.EveryProgramme;

    public static bool ReadsFromTheLock(HeadHold hold) => hold is HeadHold.WhatIsHeld;
}
