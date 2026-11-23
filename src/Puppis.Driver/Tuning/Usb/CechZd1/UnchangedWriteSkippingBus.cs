namespace Puppis.Driver.Tuning.Usb.CechZd1;

public sealed class UnchangedWriteSkippingBus(IRegisterBus inner, IReadOnlySet<int> registers) : IRegisterBus
{
    private readonly Dictionary<int, ushort> held = [];

    public ushort Read(int register) => inner.Read(register);

    public void Write(int register, ushort value)
    {
        if (held.TryGetValue(register, out ushort last) && last == value)
        {
            return;
        }

        held.Remove(register);
        inner.Write(register, value);

        if (registers.Contains(register))
        {
            held[register] = value;
        }
    }

    public void Forget() => held.Clear();
}
