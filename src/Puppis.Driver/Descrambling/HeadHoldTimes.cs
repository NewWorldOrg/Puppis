namespace Puppis.Driver.Descrambling;

public sealed class HeadHoldTimes(TimeProvider time)
{
    private long firstByteAt;
    private long lastBytesAt;
    private bool started;

    public TimeSpan? Pat { get; private set; }

    public TimeSpan? PmtNamingAnEcm { get; private set; }

    public TimeSpan? Ecm { get; private set; }

    public TimeSpan? Answer { get; private set; }

    public bool Started => started;

    public TimeSpan SinceTheFirstByte => started ? time.GetElapsedTime(firstByteAt) : TimeSpan.Zero;

    public TimeSpan SinceTheLastBytes => started ? time.GetElapsedTime(lastBytesAt) : TimeSpan.Zero;

    public void BytesCame()
    {
        lastBytesAt = time.GetTimestamp();

        if (started)
        {
            return;
        }

        started = true;
        firstByteAt = lastBytesAt;
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
