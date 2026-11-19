using System.Text.Json.Serialization;

namespace Puppis.Contracts;

[JsonConverter(typeof(TunerHealthLevelConverter))]
public enum TunerHealthLevel
{
    Unspecified = 0,

    Healthy = 1,

    Degraded = 2,

    Faulted = 3,
}

[JsonConverter(typeof(TunerFaultKindConverter))]
public enum TunerFaultKind
{
    Unspecified = 0,

    LedgerDisagrees = 1,

    DeviceFailed = 2,

    DeviceFailedAgain = 3,

    RepeatedTuneFailure = 4,
}

[JsonConverter(typeof(TunerDegradedKindConverter))]
public enum TunerDegradedKind
{
    Unspecified = 0,

    TuneFailing = 1,
}

[JsonConverter(typeof(DeviceDetectionConverter))]
public enum DeviceDetection
{
    Unspecified = 0,

    Detected = 1,

    Busy = 2,

    PermissionDenied = 3,

    Unreadable = 4,
}

[JsonConverter(typeof(TunerBusConverter))]
public enum TunerBus
{
    Unspecified = 0,

    Pci = 1,

    Usb = 2,

    Synthetic = 3,

    Lent = 4,
}

public sealed record TunerHardwareDto
{
    public string? Name { get; init; }

    public string? KernelDriver { get; init; }

    public TunerBus Bus { get; init; }

    public string? DevicePath { get; init; }

    public string? UsbPort { get; init; }

    public bool? Present { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record TunerHealthDto
{
    private readonly IReadOnlyList<TunerKind> faultReceivableKinds = [];

    public TunerHealthLevel Level { get; init; }

    public bool DisablePending { get; init; }

    public bool LnbPowered { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset? ChangedAt { get; init; }

    public string? FaultTitle { get; init; }

    public TunerFaultKind FaultKind { get; init; }

    public TunerKind? FaultDeclaredKind { get; init; }

    public IReadOnlyList<TunerKind> FaultReceivableKinds
    {
        get => faultReceivableKinds;
        init => faultReceivableKinds = value ?? [];
    }

    public TunerDegradedKind DegradedKind { get; init; }
}

public sealed record CurrentSessionDto
{
    public SessionId SessionId { get; init; }

    public SessionPurpose Purpose { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public TuneParams? Tune { get; init; }

    public DateTimeOffset? EndsAt { get; init; }
}

public sealed record DetectedDeviceDto
{
    private readonly IReadOnlyList<TunerKind> kinds = [];

    public string DeviceId { get; init; } = string.Empty;

    public DeviceDetection Detection { get; init; }

    public IReadOnlyList<TunerKind> Kinds
    {
        get => kinds;
        init => kinds = value ?? [];
    }

    public string? Detail { get; init; }

    public string? Machine { get; init; }

    public TunerHardwareDto? Hardware { get; init; }
}
