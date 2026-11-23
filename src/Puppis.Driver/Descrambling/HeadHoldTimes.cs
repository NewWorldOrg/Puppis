namespace Puppis.Driver.Descrambling;

public sealed class HeadHoldTimes(TimeProvider time)
{
    private long firstByteAt;
    private bool started;

    public TimeSpan? Pat { get; private set; }

    public TimeSpan? PmtNamingAnEcm { get; private set; }

    public TimeSpan? Ecm { get; private set; }

    public TimeSpan? Answer { get; private set; }

    public TimeSpan SinceTheFirstByte => started ? time.GetElapsedTime(firstByteAt) : TimeSpan.Zero;

    public void FirstByte()
    {
        if (started)
        {
            return;
        }

        started = true;
        firstByteAt = time.GetTimestamp();
    }

    public void PatSeen() => Pat ??= SinceTheFirstByte;

    public void PmtNamingAnEcmSeen() => PmtNamingAnEcm ??= SinceTheFirstByte;

    public void EcmSeen() => Ecm ??= SinceTheFirstByte;

    public void Answered() => Answer ??= SinceTheFirstByte;

    public override string ToString() =>
        $"PAT {After(Pat)}, first PMT naming an ECM PID {After(PmtNamingAnEcm)}, first ECM {After(Ecm)}, first answer from the card {After(Answer)}";

    private static string After(TimeSpan? seen) =>
        seen is { } after ? $"after {after.TotalMilliseconds:0.0} ms" : "not seen";
}
