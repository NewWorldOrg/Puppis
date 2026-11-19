namespace Puppis.Driver.Tuning.Usb;

public readonly ref struct HeldTurn
{
    private readonly TurnTakingConnection? taken;
    private readonly long turn;

    internal HeldTurn(TurnTakingConnection taken, long turn)
    {
        this.taken = taken;
        this.turn = turn;
    }

    public void Dispose() => taken?.GiveBack(turn);
}
