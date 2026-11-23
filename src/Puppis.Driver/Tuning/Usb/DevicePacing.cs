namespace Puppis.Driver.Tuning.Usb;

public sealed class DevicePacing(TimeProvider time, Action<TimeSpan> sleep)
{
    private long sleeps;
    private long sleptTicks;

    public static DevicePacing RealTime(TimeProvider time) => new(time, Thread.Sleep);

    public TimeProvider Clock => time;

    public DateTimeOffset Now => time.GetUtcNow();

    public long Timestamp => time.GetTimestamp();

    public long Sleeps => Interlocked.Read(ref sleeps);

    public TimeSpan Slept => TimeSpan.FromTicks(Interlocked.Read(ref sleptTicks));

    public DevicePacing CountedOnItsOwn() => new(time, sleep);

    public TimeSpan Since(long timestamp) => time.GetElapsedTime(timestamp);

    public void Sleep(TimeSpan span)
    {
        Interlocked.Increment(ref sleeps);
        Interlocked.Add(ref sleptTicks, span.Ticks);
        sleep(span);
    }

    public void SleepMilliseconds(int milliseconds) => Sleep(TimeSpan.FromMilliseconds(milliseconds));
}
