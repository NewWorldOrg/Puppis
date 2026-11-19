using System.Text.Json.Serialization;

namespace Puppis.Contracts;

[JsonConverter(typeof(CardReaderUseConverter))]
public enum CardReaderUse
{
    Unspecified = 0,

    Local = 1,

    Borrowed = 2,

    None = 3,
}

[JsonConverter(typeof(CardStateConverter))]
public enum CardState
{
    Unspecified = 0,

    Inserted = 1,

    Empty = 2,

    Unresponsive = 3,

    Borrowed = 4,

    NoReader = 5,

    NotUsed = 6,
}

[JsonConverter(typeof(CardProblemConverter))]
public enum CardProblem
{
    Unspecified = 0,

    ServiceDown = 1,

    LibraryMissing = 2,

    CardSilent = 3,

    LenderUnreachable = 4,

    LenderRefused = 5,
}

public sealed record CardReaderSlotDto
{
    public string Name { get; init; } = string.Empty;

    public CardState State { get; init; }

    public bool InUse { get; init; }
}

public sealed record CardReaderDto
{
    private readonly IReadOnlyList<CardReaderSlotDto> readers = [];

    public CardReaderUse Use { get; init; }

    public CardState State { get; init; }

    public CardProblem? Problem { get; init; }

    public IReadOnlyList<CardReaderSlotDto> Readers
    {
        get => readers;
        init => readers = value ?? [];
    }

    public string? LenderHostName { get; init; }

    public int? LeasesOut { get; init; }

    public DateTimeOffset? LastAnsweredAt { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }
}
