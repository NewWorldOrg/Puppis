using System.Text.Json.Serialization;

namespace Puppis.Contracts;

[JsonConverter(typeof(SessionStopReasonConverter))]
public enum SessionStopReason
{
    Unspecified = 0,

    Running = 1,

    Requested = 2,

    EndTimeReached = 3,

    DrainCapReached = 4,

    DeviceFailed = 5,

    RecordingFailed = 6,

    Preempted = 7,
}
