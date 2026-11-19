namespace Puppis.Driver.Descrambling;

public enum DescramblingFailure
{
    Unspecified = 0,

    CardSilent = 1,

    LenderUnreachable = 2,

    LenderRefused = 3,
}

public sealed class DescramblingException(string message, DescramblingFailure failure = DescramblingFailure.Unspecified)
    : IOException(message)
{
    public DescramblingFailure Failure { get; } = failure;
}

public interface IDescrambler : IDisposable
{
    byte[] Descramble(ReadOnlySpan<byte> stream);

    byte[] WhatItCouldNotRead();
}

public interface IDescramblerFactory
{
    bool Unscrambles { get; }

    CardIdentity? CardInHand { get; }

    IDescrambler? Open(HeadHold hold);
}

public sealed class NoDescrambling : IDescramblerFactory
{
    public static readonly NoDescrambling Instance = new();

    private NoDescrambling() { }

    public bool Unscrambles => false;

    public CardIdentity? CardInHand => null;

    public IDescrambler? Open(HeadHold hold) => null;
}
