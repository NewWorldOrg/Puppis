using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295Capture(BridgeWindow window, BridgeMemory memory)
{
    public const byte Endpoint = 0x81;

    public const int TransferBytes = 1536 * 42;

    public const int TransfersInFlight = 16;

    public const uint Clocked = 0x01900404;

    public static readonly IReadOnlyList<(uint Address, uint Value, bool Checked)> Readying =
    [
        (0x01900004, 0x001F11FF, false),
        (0x01900000, 0x3002FFFF, false),
        (0x01900000, 0x00020000, false),
        (0x01900004, 0x00001100, false),
        (0x01901040, 0, true),
        (0x01901064, 0, true),
        (0x019010A0, 0, true),
        (0x019010A4, 0, true),
        (0x019010A8, 0, true),
        (0x01901000, 0x06000000, true),
        (0x01901020, 0x02000000, true),
        (0x01901004, 0x04000000, true),
        (0x01901044, 0x00BC0000, true),
        (0x01901048, 0x47000000, true),
        (0x0190104C, 0x00FFFFFF, true),
        (0x01901050, 0, true),
        (0x01901060, 0, true),
        (0x01901080, 0x01000000, true),
        (0x01901088, 0, true),
        (0x0190108C, 0xFFFFFFFF, true),
        (0x01901084, 0, true),
        (0x01901090, 0x01000000, true),
        (0x01902020, 0x0138EC00, true),
        (0x01902024, CopyProgram.RingBytes, true),
        (0x01902028, 0, true),
        (0x0190202C, 0, true),
        (0x01902030, 0, true),
        (0x01902000, 0x03020300, true),
    ];

    public static readonly IReadOnlyList<(uint Address, uint Value)> Opening =
    [
        (0x01901000, 0x06070000),
        (0x01901040, 0),
        (0x01901020, 0),
        (0x01901040, 0x03000000),
        (0x019010A4, 0x13170400),
        (CopyProgram.CaptureRunning, CopyProgram.Running),
    ];

    public static readonly IReadOnlyList<(uint Address, uint Value)> Closing =
    [
        (0x01901040, 0),
        (0x01901020, 0x02000000),
        (CopyProgram.CaptureRunning, 0x04000000),
    ];

    private const uint EndpointMode = 0xFDBC04;
    private const uint EndpointBuffer = 0xFDBC14;
    private const uint EndpointReset = 0xFDBD08;

    public void Prepare()
    {
        window.WriteAndCheck(Clocked, window.Read(Clocked) | 0x00040000);

        foreach ((uint at, uint value, bool check) in Readying)
        {
            if (check)
            {
                window.WriteAndCheck(at, value);
            }
            else
            {
                window.Write(at, value);
            }
        }
    }

    public EndpointState KeepTheEndpoint()
    {
        var kept = new EndpointState(memory.Read(EndpointMode), memory.Read(EndpointBuffer), memory.Read(EndpointReset));
        RequireGroupAtRest();

        return kept;
    }

    public void ReadyTheEndpoint(EndpointState kept)
    {
        InGroup(1, EndpointMode, 0x85000000);
        InGroup(8, EndpointReset, 1);
        InGroup(6, EndpointReset, 0x00FF6000);
        InGroup(6, EndpointReset, memory.Read(EndpointReset));
        uint buffer = kept.Buffer & 0xFFFDFFFD;
        InGroup(2, EndpointBuffer, buffer);
        InGroup(8, EndpointBuffer, buffer);
    }

    public void RestoreTheEndpoint(EndpointState kept)
    {
        List<string> failed = [];
        Attempt(failed, () => InGroup(8, EndpointReset, 1));
        Attempt(failed, () => InGroup(6, EndpointReset, kept.Reset));
        Attempt(failed, () => InGroup(2, EndpointBuffer, kept.Buffer));
        Attempt(failed, () => InGroup(8, EndpointBuffer, kept.Buffer));
        Attempt(failed, () => InGroup(1, EndpointMode, kept.Mode));
        Attempt(failed, RequireGroupAtRest);

        if (failed.Count > 0)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: {failed.Count} of the steps putting the endpoint back failed: {string.Join("; ", failed)}"
            );
        }
    }

    private static void Attempt(List<string> failed, Action step)
    {
        try
        {
            step();
        }
        catch (DvbDeviceException fault)
        {
            failed.Add(fault.Message);
        }
    }

    public void Start()
    {
        foreach ((uint at, uint value) in Opening)
        {
            window.Write(at, value);
        }
    }

    public void Stop()
    {
        foreach ((uint at, uint value) in Closing)
        {
            window.Write(at, value);
        }
    }

    public uint Writer() => window.Read(CopyProgram.WriterPosition);

    private void InGroup(uint group, uint at, uint value)
    {
        try
        {
            memory.Write(CopyProgram.UsbGroup, group);
            memory.Write(at, value);
        }
        finally
        {
            memory.Write(CopyProgram.UsbGroup, CopyProgram.UsbGroupAtRest);
        }
    }

    private void RequireGroupAtRest()
    {
        uint group = memory.Read(CopyProgram.UsbGroup);

        if (group is not CopyProgram.UsbGroupAtRest)
        {
            throw DvbFailure.Refused(
                $"{memory.Path}: the USB core's register group reads {group} rather than {CopyProgram.UsbGroupAtRest}, so the endpoint is left as it is."
            );
        }
    }
}

public sealed record EndpointState(uint Mode, uint Buffer, uint Reset);
