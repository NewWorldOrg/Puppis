namespace Puppis.Driver.Tuning.Usb;

public sealed class DevicePacing(TimeProvider time, Action<TimeSpan> sleep)
{
    public static DevicePacing RealTime(TimeProvider time) => new(time, Thread.Sleep);

    public DateTimeOffset Now => time.GetUtcNow();

    public long Timestamp => time.GetTimestamp();

    public TimeSpan Since(long timestamp) => time.GetElapsedTime(timestamp);

    public void Sleep(TimeSpan span) => sleep(span);

    public void SleepMilliseconds(int milliseconds) => sleep(TimeSpan.FromMilliseconds(milliseconds));
}
