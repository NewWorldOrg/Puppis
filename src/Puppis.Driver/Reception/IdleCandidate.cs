namespace Puppis.Driver.Reception;

public sealed record IdleCandidate(
    string DeviceId,
    bool Still,
    bool Ready,
    ReceptionStanding Standing,
    bool HeldForAReturn = false
);
