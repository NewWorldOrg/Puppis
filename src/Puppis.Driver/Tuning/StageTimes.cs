using Puppis.Driver.Tuning.Usb;

namespace Puppis.Driver.Tuning;

public sealed class StageTimes(TimeProvider time)
{
    private readonly List<Stage> stages = [];
    private readonly List<string> notes = [];
    private readonly long started = time.GetTimestamp();
    private long last = time.GetTimestamp();
    private Func<long>? requests;
    private DevicePacing? pacing;
    private long requestsAtLast;
    private long sleepsAtLast;
    private TimeSpan sleptAtLast;

    public TimeSpan Total => time.GetElapsedTime(started, last);

    public void Count(Func<long> requestsSent, DevicePacing sleepsOn)
    {
        requests = requestsSent;
        pacing = sleepsOn;
        requestsAtLast = requestsSent();
        sleepsAtLast = sleepsOn.Sleeps;
        sleptAtLast = sleepsOn.Slept;
    }

    public void Mark(string stage, string? detail = null)
    {
        long now = time.GetTimestamp();
        Stage marked = Named(stage);
        marked.Took += time.GetElapsedTime(last, now);
        marked.Detail = detail ?? marked.Detail;
        last = now;

        if (requests is null || pacing is null)
        {
            return;
        }

        long sent = requests();
        long sleeps = pacing.Sleeps;
        TimeSpan slept = pacing.Slept;
        marked.Requests = (marked.Requests ?? 0) + sent - requestsAtLast;
        marked.Sleeps = (marked.Sleeps ?? 0) + sleeps - sleepsAtLast;
        marked.Slept += slept - sleptAtLast;
        requestsAtLast = sent;
        sleepsAtLast = sleeps;
        sleptAtLast = slept;
    }

    public void Note(string note) => notes.Add(note);

    public override string ToString()
    {
        string all = $"{Total.TotalMilliseconds:0.0} ms in all";
        string listed = stages.Count is 0 ? all : $"{all} ({string.Join(", ", stages)})";

        return notes.Count is 0 ? listed : $"{listed}; {string.Join("; ", notes)}";
    }

    private Stage Named(string name)
    {
        Stage? known = stages.Find(stage => stage.Name == name);

        if (known is not null)
        {
            return known;
        }

        Stage added = new(name);
        stages.Add(added);

        return added;
    }

    private sealed class Stage(string name)
    {
        public string Name { get; } = name;

        public TimeSpan Took { get; set; }

        public long? Requests { get; set; }

        public long? Sleeps { get; set; }

        public TimeSpan Slept { get; set; }

        public string? Detail { get; set; }

        public override string ToString()
        {
            string took = $"{Name} {Took.TotalMilliseconds:0.0} ms";
            string counted = Requests is { } sent && Sleeps is { } sleeps
                ? $"{took}/{sent} requests/{Slept.TotalMilliseconds:0.0} ms in {sleeps} sleeps"
                : took;

            return Detail is null ? counted : $"{counted}/{Detail}";
        }
    }
}
