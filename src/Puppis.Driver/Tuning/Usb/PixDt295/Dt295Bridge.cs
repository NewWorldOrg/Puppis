using Microsoft.Extensions.Logging;

using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// An open PIX-DT295 with its bridge started, its chips set up and the copying program hooked in: everything a
/// session needs before it tunes. It outlives the sessions on it, and is given back as it was found when it closes.
/// </summary>
public sealed class Dt295Bridge
{
    private readonly TurnTakingConnection usb;
    private readonly DevicePacing pacing;
    private readonly BridgeMemory memory;
    private readonly BridgeWindow window;
    private readonly Dt295Capture capture;
    private readonly Fr30Program program = CopyProgram.Build();
    private readonly ILogger? logger;
    private readonly Lock gate = new();
    private bool started;
    private bool closed;
    private bool capturing;
    private EndpointState? endpoint;
    private IUsbBulkReader? reader;
    private Dt295Stream? stream;

    private Dt295Bridge(TurnTakingConnection usb, DevicePacing pacing, ILogger? logger)
    {
        this.usb = usb;
        this.pacing = pacing;
        this.logger = logger;
        memory = new BridgeMemory(usb);
        window = new BridgeWindow(memory);
        capture = new Dt295Capture(window, memory);
        Frontend = new Dt295Frontend(new Dt295Chips(new BridgeI2c(memory)), pacing);
        Slot = new Dt295CardSlot(new CardUart(new BridgeMemory(usb), pacing));
    }

    public string Path => usb.Path;

    public Dt295Frontend Frontend { get; }

    public Dt295CardSlot Slot { get; }

    /// <summary>
    /// Whether a session's stream is running on the bridge.
    /// </summary>
    public bool InSession
    {
        get
        {
            lock (gate)
            {
                return !closed && stream is not null;
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return !closed;
            }
        }
    }

    /// <summary>
    /// The card slot as a smart-card service, valid while the bridge is open.
    /// </summary>
    public ISmartCardService CardService() => Slot.Service();

    /// <summary>
    /// Opens the device and brings it from power on to ready for tuning, or closes it again, given back, when any step
    /// fails.
    /// </summary>
    public static Dt295Bridge Open(
        IUsbHost host,
        string path,
        DevicePacing pacing,
        ILogger? logger = null,
        string knownHandler = Dt295Startup.HandlerAtPowerOn
    )
    {
        UsbModel model = host.ModelAt(path);

        if (model != Dt295TunerDevice.Model)
        {
            throw DvbFailure.Refused($"{path}: the device there is {model}, not the {Dt295TunerDevice.Model} tuner this driver opened it as.");
        }

        Dt295Bridge bridge = new(new TurnTakingConnection(host.Open(path, Dt295TunerDevice.Interface)), pacing, logger);

        try
        {
            bridge.Start(knownHandler);

            return bridge;
        }
        catch
        {
            bridge.Close();

            throw;
        }
    }

    /// <summary>
    /// Whether the device is still as it was left: the hook and the window words in place, the USB core's register
    /// group at rest, and the terrestrial tuner answering on the I2C bus. A failed request answers false.
    /// </summary>
    public bool StillAsLeft()
    {
        try
        {
            uint[] hook = memory.ReadWords(CopyProgram.HookAt, 2);

            return CopyProgram.IsOurHook(hook[0], hook[1])
                && memory.Read(CopyProgram.UsbGroup) is CopyProgram.UsbGroupAtRest
                && memory.Read(BridgeWindow.ReadWindow) is BridgeWindow.AtPowerOn
                && memory.Read(BridgeWindow.WriteWindow) is BridgeWindow.AtPowerOn
                && Frontend.Chips.ReadRT(0x18) is Dt295Frontend.TunerIdentity;
        }
        catch (Exception failed) when (failed is DvbDeviceException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Readies the capture block and the endpoint, puts the reads on the endpoint and starts the stream.
    /// </summary>
    public Dt295Stream StartStream()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            capture.Prepare();
            endpoint = capture.KeepTheEndpoint();
            capture.ReadyTheEndpoint(endpoint);
            reader = usb.ReadBulk(Dt295Capture.Endpoint, Dt295Capture.TransferBytes, Dt295Capture.TransfersInFlight);
            capturing = true;
            capture.Start();
            stream = new Dt295Stream(usb, new BridgeMemory(usb), capture.Writer, reader, pacing);
            stream.Start();

            return stream;
        }
    }

    /// <summary>
    /// Ends the stream as the bridge's capture expects and puts the endpoint back. Answers whether every step went
    /// through, which is what keeping the device open for the next session asks for.
    /// </summary>
    public bool StopStream()
    {
        lock (gate)
        {
            bool stopped = Quietly("stopping the copying", () => stream?.Dispose());

            if (capturing)
            {
                stopped &= Quietly("stopping the capture", capture.Stop);
            }

            stopped &= Quietly("cancelling the bulk reads", () => reader?.Dispose());

            if (endpoint is not null)
            {
                stopped &= Quietly("putting the endpoint back", () => capture.RestoreTheEndpoint(endpoint));
            }

            stream = null;
            reader = null;
            endpoint = null;
            capturing = false;

            return stopped;
        }
    }

    /// <summary>
    /// Puts whichever side was tuned back to how the set-up leaves it, ready for the next tune, and switches the card
    /// off. Answers whether it went through.
    /// </summary>
    public bool Rest() =>
        Quietly(
            "putting the tuner to rest",
            () =>
            {
                switch (Frontend.Tuned)
                {
                    case Dt295Side.Terrestrial:
                        Frontend.LeaveTheTerrestrialSide();
                        break;
                    case Dt295Side.Satellite:
                        Frontend.SleepTheSatelliteSide();
                        break;
                }
            }
        ) & Quietly("switching the card off", Slot.Rest);

    /// <summary>
    /// Gives the bridge back step by step, carrying on past a step that fails and saying which one did, and closes
    /// the device.
    /// </summary>
    public void Close()
    {
        lock (gate)
        {
            if (closed)
            {
                return;
            }

            closed = true;
        }

        StopStream();

        if (started)
        {
            Quietly("taking the hook out", () => CopyProgram.UnhookIfOurs(memory));
            Quietly("switching the card off", Slot.Close);
            Quietly("putting the window back", window.PutBack);
        }

        usb.Dispose();
    }

    private void Start(string knownHandler)
    {
        Dt295Startup.RequirePowerOnState(memory);
        Dt295Startup.RequireTheKnownHandler(memory, knownHandler);
        started = true;
        Dt295Startup.ResetTheDemodulator(memory, pacing);
        Dt295Startup.StartClocks(memory);
        Dt295Startup.StartI2cMaster(memory);
        Frontend.Identify(usb.Path);
        Frontend.Initialise();
        CopyProgram.Install(memory, program, () => Dt295Stream.Instruct(usb, memory, 0, CopyCommand.Answer, 0, 0));
    }

    private bool Quietly(string step, Action action)
    {
        try
        {
            action();

            return true;
        }
        catch (Exception failed) when (failed is DvbDeviceException or InvalidOperationException)
        {
            logger?.LogWarning("{Path}: {Step} failed: {Why}", usb.Path, step, failed.GetType().Name);

            return false;
        }
    }
}
