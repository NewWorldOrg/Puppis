namespace Puppis.Contracts;

public sealed record ConditionalAccessCardDto
{
    public int CaSystemId { get; init; }

    public string CardId { get; init; } = string.Empty;

    public override string ToString() => $"a card of CA system 0x{CaSystemId:X4}";
}
