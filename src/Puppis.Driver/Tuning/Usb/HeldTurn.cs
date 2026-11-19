namespace Puppis.Driver.Tuning.Usb;

public readonly ref struct HeldTurn
{
    private readonly TurnTakingConnection? taken;

    internal HeldTurn(TurnTakingConnection taken) => this.taken = taken;

    public void Dispose() => taken?.GiveBack();
}
