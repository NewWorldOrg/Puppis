namespace Puppis.Driver.Tuning.Usb;

/// <summary>
/// The clock and the sleep a device is driven by, so that a test can stand in for both.
/// </summary>
public sealed class DevicePacing(TimeProvider time, Action<TimeSpan> sleep)
{
    public static DevicePacing RealTime(TimeProvider time) => new(time, Thread.Sleep);

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// A reading of the clock that only moves forwards, for measuring how long something took.
    /// </summary>
    public long Timestamp => time.GetTimestamp();

    public TimeSpan Since(long timestamp) => time.GetElapsedTime(timestamp);

    public void Sleep(TimeSpan span) => sleep(span);

    public void SleepMilliseconds(int milliseconds) => sleep(TimeSpan.FromMilliseconds(milliseconds));
}
