namespace Puppis.Driver.Tuning.Usb;

public readonly ref struct HeldTurn(TurnTakingConnection? taken)
{
    public void Dispose() => taken?.GiveBack();
}
