namespace Puppis.Contracts;

public sealed record DriverRestartDto
{
    public string? InstanceId { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }

    public int BudgetSeconds { get; init; }
}
