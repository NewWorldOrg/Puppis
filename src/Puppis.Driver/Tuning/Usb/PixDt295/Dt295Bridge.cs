using Microsoft.Extensions.Logging;

using Puppis.Driver.Descrambling;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

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
    private volatile bool streaming;
    private bool captureReady;
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

    public bool InSession => streaming;

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

    public ISmartCardService CardService() => Slot.Service();

    public static Dt295Bridge Open(
        IUsbHost host,
        string path,
        DevicePacing pacing,
        ILogger? logger = null,
        string knownHandler = Dt295Startup.HandlerAtPowerOn,
        StageTimes? stages = null
    )
    {
        UsbModel model = host.ModelAt(path);

        if (model != Dt295TunerDevice.Model)
        {
            throw DvbFailure.Refused($"{path}: the device there is {model}, not the {Dt295TunerDevice.Model} tuner this driver opened it as.");
        }

        Dt295Bridge bridge = new(new TurnTakingConnection(host.Open(path, Dt295TunerDevice.Interface)), pacing, logger);
        stages?.Mark("USB open");

        if (stages is not null)
        {
            bridge.Count(stages);
        }

        try
        {
            bridge.Start(knownHandler, stages);

            return bridge;
        }
        catch
        {
            bridge.Close();

            throw;
        }
    }

    public void Count(StageTimes stages) => stages.Count(() => memory.Requests, Frontend.Pacing);

    public bool StillAsLeft(StageTimes? stages = null)
    {
        if (stages is not null)
        {
            Count(stages);
        }

        bool asLeft = ReadsAsItWasLeft();
        stages?.Mark("kept bridge check");

        return asLeft;
    }

    private bool ReadsAsItWasLeft()
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

    public TuneWaits ReadyingTheStream(StageTimes? stages = null) =>
        new(() => ReadyTheCapture(stages), () => ReadyTheEndpoint(stages));

    public void ReadyTheCapture(StageTimes? stages = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);

            if (captureReady)
            {
                return;
            }

            capture.Prepare();
            captureReady = true;
            stages?.Mark("capture set-up");
        }
    }

    public void ReadyTheEndpoint(StageTimes? stages = null) => ReadsWaiting(stages);

    public Dt295Stream StartStream(StageTimes? stages = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            ReadyTheCapture(stages);
            IUsbBulkReader waiting = ReadsWaiting(stages);
            capturing = true;
            capture.Start();
            stages?.Mark("intake");
            stream = new Dt295Stream(usb, new BridgeMemory(usb), capture.Writer, waiting, pacing);
            stream.Start();
            streaming = true;
            stages?.Mark("copying start");

            return stream;
        }
    }

    private IUsbBulkReader ReadsWaiting(StageTimes? stages)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);

            if (reader is not null)
            {
                return reader;
            }

            endpoint = capture.KeepTheEndpoint();
            capture.ReadyTheEndpoint(endpoint);
            stages?.Mark("endpoint set-up");
            IUsbBulkReader waiting = usb.ReadBulk(Dt295Capture.Endpoint, Dt295Capture.TransferBytes, Dt295Capture.TransfersInFlight);
            reader = waiting;
            stages?.Mark("bulk reads");

            return waiting;
        }
    }

    public bool StopStream()
    {
        streaming = false;

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
            captureReady = false;
            capturing = false;

            return stopped;
        }
    }

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

        Quietly("closing the device", usb.Dispose);
    }

    private void Start(string knownHandler, StageTimes? stages)
    {
        Dt295Startup.RequirePowerOnState(memory);
        Dt295Startup.RequireTheKnownHandler(memory, knownHandler);
        started = true;
        stages?.Mark("handler check");
        Dt295Startup.ResetTheDemodulator(memory, Frontend.Pacing);
        stages?.Mark("demodulator reset");
        Dt295Startup.StartClocks(memory);
        Dt295Startup.StartI2cMaster(memory);
        stages?.Mark("clocks and I2C");
        Frontend.Identify(usb.Path);
        Frontend.Initialise();
        stages?.Mark("chip initialisation");
        CopyProgram.Install(memory, program, () => Dt295Stream.Instruct(usb, memory, 0, CopyCommand.Answer, 0, 0));
        stages?.Mark("copy program");
    }

    private bool Quietly(string step, Action action)
    {
        try
        {
            action();

            return true;
        }
        catch (Exception failed) when (failed is not OutOfMemoryException)
        {
            logger?.LogWarning("{Path}: {Step} failed: {Why}", usb.Path, step, failed.GetType().Name);

            return false;
        }
    }
}
