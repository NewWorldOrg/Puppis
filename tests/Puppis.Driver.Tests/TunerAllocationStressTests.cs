using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Reception;
using Puppis.Driver.Recording;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

using Xunit.Abstractions;

namespace Puppis.Driver.Tests;

public sealed class TunerAllocationStressTests(ITestOutputHelper output)
{
    private const int Workers = 8;

    private const int StepsPerWorker = 150;

    private const string SeedsVariable = "PUPPIS_ALLOCATION_SEEDS";

    private static readonly TimeSpan Forever = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Unseated = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan StuckOpen = TimeSpan.FromMilliseconds(150);

    private static readonly string[] Tuners = ["t0", "t1", "t2", "t3"];

    private static readonly int[] Channels = [20, 21, 22];

    private static readonly int[] ChannelsToMeasure = [.. Enumerable.Range(13, 40)];

    private const string CausedByAMeasurement = TunerPool.MeasurementUnderWay;

    private const int ClockSpeed = 300;

    private static readonly TimeSpan OnlyAStuckMeasurementTakesThisLong = TimeSpan.FromSeconds(20);

    private static readonly SessionPurpose[] Purposes =
    [
        SessionPurpose.Recording,
        SessionPurpose.Live,
        SessionPurpose.Scan,
        SessionPurpose.Survey,
        SessionPurpose.SurveyNow,
        SessionPurpose.Logo,
    ];

    public static TheoryData<int> Seeds()
    {
        TheoryData<int> seeds = [];
        string? asked = Environment.GetEnvironmentVariable(SeedsVariable);

        if (asked?.Split(':') is [{ } from, { } count])
        {
            int first = int.Parse(from, CultureInfo.InvariantCulture);

            foreach (int seed in Enumerable.Range(first, int.Parse(count, CultureInfo.InvariantCulture)))
            {
                seeds.Add(seed);
            }

            return seeds;
        }

        seeds.Add(1);
        seeds.Add(2);
        seeds.Add(3);

        return seeds;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public Task ManyCallersAtOnceNeverBreakWhoHoldsWhichTuner(int seed) => PlayAsync(seed, sessionsAreShort: false);

    [Theory]
    [MemberData(nameof(Seeds))]
    public Task IdleTunersAreMeasuredBetweenRequestsWithoutBreakingWhoHoldsWhichTuner(int seed) =>
        PlayAsync(seed, sessionsAreShort: true);

    private async Task PlayAsync(int seed, bool sessionsAreShort)
    {
        using var run = new Run(seed, sessionsAreShort);

        try
        {
            await run.PlayAsync();
            output.WriteLine(
                $"seed {seed}: {run.LeftMidOpen} requests left while their tuner was being opened, "
                + $"{run.MeasurementsInterrupted} measurements were interrupted and {run.MeasurementsFinished} ran to their end, "
                + $"{run.Refusals.Count} requests were refused ({run.RefusalsByKind}) and {run.RefusedBecauseOfAMeasurement.Count} of them because of a measurement"
            );
        }
        catch (Exception failure) when (failure is not SeededFailure)
        {
            throw new SeededFailure(seed, failure.Message, failure);
        }
    }

    private static IEnumerable<string> WhatAMeasurementCaused(IEnumerable<string> refusalsAndErrors) =>
        refusalsAndErrors.Where(said => said.Contains(CausedByAMeasurement, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusalCausedByAMeasurementIsOneTheRunWouldFailOn(bool namesTheTuner)
    {
        var devices = new StuckWhileMeasured();
        var log = new RefusalLog();
        var book = new ReceptionBook();
        var configuration = new DriverConfiguration(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", Path.GetTempPath())],
            6,
            new TunerSettings(TunerBackend.Fake),
            [new DeviceSettings("t0", DeviceKind.Terrestrial)]
        );
        var manager = new TunerSessionManager(configuration, devices, TimeProvider.System, log, openLimit: TimeSpan.FromMilliseconds(100), reception: book);
        var measurer = new IdleReceptionMeasurer(
            configuration,
            manager,
            book,
            devices,
            TimeProvider.System,
            new MeasurerLog(),
            restBeforeMeasuring: TimeSpan.Zero
        );
        book.Know(new TuningKey(TunerKind.Terrestrial, 30, 0));
        Task measuring = Task.Run(measurer.MeasureIfDue);
        await Task.Run(() => Assert.True(devices.Measuring.Wait(Forever)));

        SessionStart refused = await manager
            .BeginAsync(
                new StartSessionRequest
                {
                    SessionId = SessionId.Parse("asked"),
                    Purpose = SessionPurpose.Live,
                    Tuning = new TuningRequest(TunerKind.Terrestrial, 20, 50001),
                    DeviceId = namesTheTuner ? "t0" : null,
                }
            )
            .WaitAsync(Forever);
        devices.Release.Set();
        await measuring.WaitAsync(Forever);

        Assert.NotEqual(SessionRefusal.None, refused.Refusal);
        Assert.NotEmpty(WhatAMeasurementCaused([refused.Detail]));
        Assert.NotEmpty(WhatAMeasurementCaused(log.Errors));
    }

    [Fact]
    public void ARefusalThatNoMeasurementCausedIsNotOneTheRunWouldFailOn()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("t0", true)]);
        pool.Acquire(new PoolRequest(SessionId.Parse("holder"), SessionPurpose.Recording, new TuningKey(TunerKind.Terrestrial, 20, 0), null, ["t0"]));

        PoolGrant refused = pool.Acquire(
            new PoolRequest(SessionId.Parse("asked"), SessionPurpose.Live, new TuningKey(TunerKind.Terrestrial, 21, 0), null, ["t0"])
        );

        Assert.Equal(PoolVerdict.NoDeviceFree, refused.Verdict);
        Assert.Empty(WhatAMeasurementCaused([refused.Detail]));
    }

    private sealed class StuckWhileMeasured : ITunerDeviceFactory, IReceptionProbeFactory
    {
        public ManualResetEventSlim Measuring { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold) =>
            new FakeTunerDevice(tuning.PhysicalChannel);

        public IReceptionProbe Tune(DeviceSettings device, TuneParams tune, CancellationToken cancellationToken)
        {
            Measuring.Set();
            Release.Wait(Forever);

            return new SyntheticReceptionProbe(tune.ToLegacyRequest().PhysicalChannel);
        }
    }

    private sealed class SeededFailure(int seed, string message, Exception? inner = null)
        : Exception($"seed {seed}: {message}", inner);

    private sealed class Run : IDisposable
    {
        private readonly int seed;
        private readonly bool sessionsAreShort;
        private readonly AcceleratedClock clock = new(ClockSpeed);
        private readonly DevicesUnderTest devices;
        private readonly RefusalLog log = new();
        private readonly DriverConfiguration configuration;
        private readonly TunerSessionManager manager;
        private readonly ReceptionBook book = new();
        private readonly IdleReceptionMeasurer measurer;
        private readonly ConcurrentDictionary<SessionId, int> channelOf = new();
        private readonly ConcurrentQueue<string> violations = new();
        private readonly ConcurrentDictionary<string, bool> plugged = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<SessionId, DateTime> unseatedSince = new();
        private readonly ConcurrentQueue<SessionStart> refusals = new();
        private int begun;
        private int leftMidOpen;

        public int LeftMidOpen => Volatile.Read(ref leftMidOpen);

        public long MeasurementsInterrupted => measurer.Interrupted;

        public long MeasurementsFinished => measurer.Finished;

        public IReadOnlyCollection<SessionStart> Refusals => refusals;

        public string RefusalsByKind =>
            string.Join(
                ", ",
                refusals
                    .GroupBy(refusal => refusal.Refusal)
                    .OrderBy(kind => kind.Key.ToString(), StringComparer.Ordinal)
                    .Select(kind => $"{kind.Key} {kind.Count()}")
            );

        public IReadOnlyList<string> RefusedBecauseOfAMeasurement =>
            [.. WhatAMeasurementCaused(refusals.Select(refusal => refusal.Detail).Concat(log.Errors))];

        public Run(int seed, bool sessionsAreShort)
        {
            this.seed = seed;
            this.sessionsAreShort = sessionsAreShort;
            devices = new DevicesUnderTest(seed, violations);
            configuration = new DriverConfiguration(
                "/run/carina/driver.sock",
                [new OutputRootSettings("primary", Path.GetTempPath())],
                6,
                new TunerSettings(TunerBackend.Fake),
                [.. Tuners.Select(id => new DeviceSettings(id, DeviceKind.Terrestrial))]
            );
            manager = new TunerSessionManager(
                configuration,
                devices,
                clock,
                log,
                recordingWriters: new DiscardingWriters(),
                tunerGrace: TimeSpan.FromSeconds(5),
                handOverLimit: TimeSpan.FromSeconds(30),
                deviceCheck: devices,
                presence: device => plugged.GetValueOrDefault(device.Id!, true),
                reception: book,
                measurementStopLimit: OnlyAStuckMeasurementTakesThisLong * ClockSpeed
            );
            measurer = new IdleReceptionMeasurer(
                configuration,
                manager,
                book,
                devices,
                clock,
                new MeasurerLog(),
                betweenReadings: TimeSpan.FromMilliseconds(1),
                restBeforeMeasuring: TimeSpan.FromMilliseconds(30),
                betweenMeasurements: TimeSpan.FromMilliseconds(30),
                looksEvery: TimeSpan.FromMilliseconds(300)
            );

            foreach (int channel in ChannelsToMeasure)
            {
                book.Know(new TuningKey(TunerKind.Terrestrial, channel, 0));
            }
        }

        public async Task PlayAsync()
        {
            await manager.StartAsync(CancellationToken.None);
            await measurer.StartAsync(CancellationToken.None);
            await TheFirstMeasurementAsync();
            await LeaveMidOpenAsync();

            Task[] workers = [.. Enumerable.Range(0, Workers).Select(worker => Task.Run(() => WorkAsync(worker)))];

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(5));

            Check("after the workers finished");
            await AgreesWithTheDevicesAsync("after the workers finished");

            foreach (TunerSession session in manager.Sessions)
            {
                session.Stop();
            }

            foreach (TunerSession session in manager.Sessions)
            {
                await session.Completion.WaitAsync(Forever);
            }

            Check("once every session had stopped");
            await AgreesWithTheDevicesAsync("once every session had stopped");

            await measurer.StopAsync(CancellationToken.None).WaitAsync(Forever);
            await manager.DrainAsync(CancellationToken.None).WaitAsync(Forever);

            if (devices.StillOpen() is { Count: > 0 } open)
            {
                violations.Enqueue($"devices still open after the manager was disposed: {string.Join(", ", open)}");
            }

            if (devices.ProbesStillOpen() is { Count: > 0 } probing)
            {
                violations.Enqueue($"tuners still being measured after the measuring was stopped: {string.Join(", ", probing)}");
            }

            foreach (string refused in RefusedBecauseOfAMeasurement.Take(3))
            {
                violations.Enqueue($"a request was refused because of a measurement: {refused}");
            }

            if (MeasurementsInterrupted + MeasurementsFinished is 0)
            {
                violations.Enqueue("no tuner was measured while the requests ran");
            }

            if (!log.Refusals.IsEmpty)
            {
                violations.Enqueue($"the table refused changes: {string.Join(" | ", log.Refusals.Take(3))}");
            }

            if (LeftMidOpen is 0)
            {
                violations.Enqueue("no request left while its tuner was being opened");
            }

            if (!violations.IsEmpty)
            {
                throw new SeededFailure(seed, string.Join(Environment.NewLine, violations.Take(10)));
            }
        }

        private async Task TheFirstMeasurementAsync()
        {
            DateTime giveUp = DateTime.UtcNow + Forever;

            while (MeasurementsFinished + MeasurementsInterrupted is 0 && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(1);
            }
        }

        private async Task WorkAsync(int worker)
        {
            var random = new Random(HashCode.Combine(seed, worker));

            for (int step = 0; step < StepsPerWorker; step++)
            {
                int roll = StopsMoreOften(random, random.Next(100));

                if (roll < 37)
                {
                    await BeginAsync(random, worker, Pick(random, Purposes), Pick(random, Channels));
                }
                else if (roll < 45)
                {
                    int channel = Pick(random, Channels);

                    await Task.WhenAll(
                        BeginAsync(random, worker, SessionPurpose.Live, channel),
                        BeginAsync(random, worker, SessionPurpose.Recording, channel)
                    );
                }
                else if (roll < 65)
                {
                    await StopAsync(random);
                }
                else if (roll < 66)
                {
                    devices.FailOne(random);
                }
                else if (roll < 84)
                {
                    manager.Turn(Pick(random, Tuners), disabled: random.Next(4) is 0);
                }
                else if (roll < 94)
                {
                    DeviceSettings device = configuration.Devices![random.Next(Tuners.Length)];
                    bool present = random.Next(5) is not 0;

                    plugged[device.Id!] = present;

                    if (random.Next(2) is 0)
                    {
                        manager.Looked(device, present);
                    }
                }
                else
                {
                    await Task.Delay(random.Next(1, 20));
                }

                Check($"worker {worker} step {step}");
                await Task.Delay(random.Next(0, 3));
            }
        }

        private int StopsMoreOften(Random random, int roll) =>
            sessionsAreShort && roll < 45 && random.Next(5) < 3 ? 50 : roll;

        private async Task BeginAsync(Random random, int worker, SessionPurpose purpose, int channel)
        {
            using var leaving = new CancellationTokenSource();
            SessionId sessionId = SessionId.Parse($"w{worker}-{Interlocked.Increment(ref begun)}");
            DateTimeOffset now = clock.GetUtcNow();

            channelOf[sessionId] = channel;

            Task<SessionStart> starting = manager.BeginAsync(
                new StartSessionRequest
                {
                    SessionId = sessionId,
                    Purpose = purpose,
                    Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
                    DeviceId = random.Next(5) is 0 ? Pick(random, Tuners) : null,
                    OutputRoot = purpose is SessionPurpose.Recording ? "primary" : null,
                    RecordingId = purpose is SessionPurpose.Recording ? sessionId.Value : null,
                    EndsAt = purpose is SessionPurpose.Recording or SessionPurpose.Live
                        ? now.AddMinutes(random.Next(1, 10))
                        : null,
                },
                leaving.Token
            );

            if (random.Next(7) is 0)
            {
                leaving.CancelAfter(random.Next(0, 5));
            }

            try
            {
                Remember(await starting.WaitAsync(Forever));
            }
            catch (OperationCanceledException) when (leaving.IsCancellationRequested)
            {
                if (devices.OpensUnderWay > 0)
                {
                    Interlocked.Increment(ref leftMidOpen);
                }

                return;
            }
            catch (TimeoutException)
            {
                violations.Enqueue($"the start of {sessionId} waited for longer than {Forever}");
            }
        }

        private void Remember(SessionStart start)
        {
            if (start.Refusal is not SessionRefusal.None)
            {
                refusals.Enqueue(start);
            }
        }

        private async Task LeaveMidOpenAsync()
        {
            using var leaving = new CancellationTokenSource();
            ManualResetEventSlim held = devices.HoldTheNextOpen();
            SessionId sessionId = SessionId.Parse("first-to-leave");

            channelOf[sessionId] = Channels[0];

            Task<SessionStart> starting = manager.BeginAsync(
                new StartSessionRequest
                {
                    SessionId = sessionId,
                    Purpose = SessionPurpose.Live,
                    Tuning = new TuningRequest(TunerKind.Terrestrial, Channels[0], 50001),
                },
                leaving.Token
            );

            if (!held.Wait(Forever))
            {
                violations.Enqueue("the held open never started");
            }

            await leaving.CancelAsync();

            try
            {
                await starting.WaitAsync(Forever);
                violations.Enqueue("the request that left mid-open was not told it had left");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref leftMidOpen);
            }

            Check("after a request left mid-open");
            devices.ReleaseTheHeldOpen();
        }

        private async Task StopAsync(Random random)
        {
            TunerSession[] running = [.. manager.Sessions];

            if (running.Length is 0)
            {
                return;
            }

            await manager.StopAsync(Pick(random, running).SessionId, "stress", CancellationToken.None);
        }

        private void Check(string when)
        {
            IReadOnlyList<TunerAllocationView> table = manager.Allocations();
            HashSet<SessionId> seated = [];

            foreach (TunerAllocationView tuner in table)
            {
                foreach (SessionId sink in tuner.Sinks.Concat(tuner.Outgoing))
                {
                    if (!seated.Add(sink))
                    {
                        violations.Enqueue($"{when}: {sink} sits on two tuners");
                    }
                }

                if ((tuner.DeviceOpen ? 1 : 0) + (tuner.PreviousDeviceOpen ? 1 : 0) + tuner.Closing + tuner.InFlight > 1)
                {
                    violations.Enqueue($"{when}: {tuner.DeviceId} has two operations on its device at once");
                }

                if (WhatIsWrongWith(tuner) is { } wrong)
                {
                    violations.Enqueue($"{when}: {tuner.DeviceId} is {tuner.State} but {wrong}");
                }
            }

            Dictionary<string, TunerAllocationView> byTuner = table.ToDictionary(tuner => tuner.DeviceId);
            DateTime now = DateTime.UtcNow;

            foreach (TunerSession session in manager.Sessions.Where(session => session.State is SessionState.Active))
            {
                TunerAllocationView tuner = byTuner[session.DeviceId];

                if (tuner.Sinks.Contains(session.SessionId))
                {
                    unseatedSince.TryRemove(session.SessionId, out _);

                    if (tuner.Tuning?.PhysicalChannel is { } channel && channel != channelOf[session.SessionId])
                    {
                        violations.Enqueue($"{when}: {session.SessionId} on {tuner.DeviceId} reads channel {channelOf[session.SessionId]} and the tuner is on {channel}");
                    }

                    continue;
                }

                if (tuner.Outgoing.Contains(session.SessionId))
                {
                    continue;
                }

                DateTime since = unseatedSince.GetOrAdd(session.SessionId, now);

                if (now - since > Unseated)
                {
                    violations.Enqueue($"{when}: {session.SessionId} has been reading {tuner.DeviceId} for {(now - since).TotalMilliseconds:0} ms without a place on it");
                }
            }
        }

        private static string? WhatIsWrongWith(TunerAllocationView tuner) =>
            tuner.State switch
            {
                TunerAllocationState.Absent or TunerAllocationState.Disabled or TunerAllocationState.Faulted or TunerAllocationState.Free
                    when tuner.Sinks.Count > 0 || tuner.Outgoing.Count > 0 || tuner.DeviceOpen || tuner.PreviousDeviceOpen =>
                    "it still has someone on it or a device open",
                TunerAllocationState.Ready when tuner.Sinks.Count > 0 || !tuner.DeviceOpen =>
                    "it has someone on it or no device open",
                TunerAllocationState.Measuring when tuner.Sinks.Count > 0 || tuner.Outgoing.Count > 0 || tuner.DeviceOpen || tuner.PreviousDeviceOpen || !tuner.Measuring =>
                    "it has someone on it, a device open or no measurement under way",
                TunerAllocationState.Tuning when tuner.Sinks.Count is 0 && !tuner.OpeningInFlight =>
                    "nobody is on it and nothing is being opened",
                TunerAllocationState.Tuning or TunerAllocationState.InUse when tuner.Sinks.Count > 0 && !tuner.Sinks.Contains(tuner.Holder) =>
                    "its owner is not among those on it",
                TunerAllocationState.InUse when tuner.Sinks.Count is 0 =>
                    "nobody is on it",
                TunerAllocationState.HandingOver when (tuner.Outgoing.Count is 0 && !tuner.PreviousOpeningInFlight) || !tuner.Sinks.Contains(tuner.Holder) =>
                    "nobody is being waited for or its new owner is not on it",
                _ => null,
            };

        private async Task AgreesWithTheDevicesAsync(string when)
        {
            DateTime giveUp = DateTime.UtcNow.AddSeconds(2);
            string[] disagreements = Disagreements();

            while (disagreements.Length > 0 && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(10);
                disagreements = Disagreements();
            }

            foreach (string disagreement in disagreements)
            {
                violations.Enqueue($"{when}: {disagreement}");
            }
        }

        private string[] Disagreements() =>
        [
            .. manager
                .Allocations()
                .Select(tuner => (Tuner: tuner, Expected: (tuner.DeviceOpen ? 1 : 0) + (tuner.PreviousDeviceOpen ? 1 : 0), Open: devices.OpenOn(tuner.DeviceId)))
                .Where(seen => seen.Expected != seen.Open)
                .Select(seen => $"{seen.Tuner.DeviceId} is {seen.Tuner.State} with {seen.Expected} devices in the table and {seen.Open} open"),
            .. manager
                .Allocations()
                .Where(tuner => !tuner.Measuring && devices.ProbesOn(tuner.DeviceId) > 0)
                .Select(tuner => $"{tuner.DeviceId} is {tuner.State} with no measurement in the table and one under way on the device"),
        ];

        private static T Pick<T>(Random random, IReadOnlyList<T> from) => from[random.Next(from.Count)];

        public void Dispose() => devices.Release();
    }

    private sealed class DevicesUnderTest(int seed, ConcurrentQueue<string> violations)
        : ITunerDeviceFactory, ITunerDeviceCheck, IReceptionProbeFactory
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, int> open = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> probing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> opening = new(StringComparer.Ordinal);
        private readonly List<Device> created = [];
        private readonly ThreadLocal<Random> random = new(() => new Random(HashCode.Combine(seed, Environment.CurrentManagedThreadId)));

        private ManualResetEventSlim? holding;
        private readonly ManualResetEventSlim heldOpen = new(false);
        private readonly ManualResetEventSlim releaseHeld = new(false);
        private int underWay;

        public int OpensUnderWay => Volatile.Read(ref underWay);

        public ManualResetEventSlim HoldTheNextOpen()
        {
            Volatile.Write(ref holding, releaseHeld);

            return heldOpen;
        }

        public void ReleaseTheHeldOpen() => releaseHeld.Set();

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            string deviceId = device.Id!;

            BeginOpening(deviceId, "opened");
            Interlocked.Increment(ref underWay);

            try
            {
                if (Interlocked.Exchange(ref holding, null) is { } release)
                {
                    heldOpen.Set();
                    release.Wait(Forever);
                }

                Thread.Sleep(random.Value!.Next(80) is 0 ? StuckOpen : TimeSpan.FromMilliseconds(random.Value!.Next(0, 6)));

                if (random.Value!.Next(60) is 0)
                {
                    throw new DvbDeviceException($"{deviceId} did not lock", TuningFailure.NoLock);
                }
            }
            catch
            {
                Interlocked.Decrement(ref underWay);
                EndOpening(deviceId, null);

                throw;
            }

            Interlocked.Decrement(ref underWay);
            var opened = new Device(this, deviceId, tuning.PhysicalChannel);
            EndOpening(deviceId, opened);

            return opened;
        }

        public void Check(DeviceSettings device)
        {
            string deviceId = device.Id!;

            BeginOpening(deviceId, "checked");

            try
            {
                Thread.Sleep(random.Value!.Next(0, 3));
            }
            finally
            {
                EndOpening(deviceId, null);
            }
        }

        public IReceptionProbe Tune(DeviceSettings device, TuneParams tune, CancellationToken cancellationToken)
        {
            string deviceId = device.Id!;

            BeginOpening(deviceId, "measured");

            try
            {
                for (int waited = random.Value!.Next(0, 6); waited > 0 && !cancellationToken.IsCancellationRequested; waited--)
                {
                    Thread.Sleep(1);
                }

                if (random.Value!.Next(15) is 0)
                {
                    throw new DvbDeviceException($"{deviceId} did not lock while it was measured", TuningFailure.NoLock);
                }

                if (random.Value!.Next(150) is 0)
                {
                    throw new IOException($"{deviceId} stopped answering while it was measured.");
                }
            }
            catch
            {
                EndOpening(deviceId, null);

                throw;
            }

            lock (gate)
            {
                opening[deviceId] = opening.GetValueOrDefault(deviceId) - 1;
                probing[deviceId] = probing.GetValueOrDefault(deviceId) + 1;
            }

            return new Probe(this, deviceId);
        }

        public int ProbesOn(string deviceId)
        {
            lock (gate)
            {
                return probing.GetValueOrDefault(deviceId);
            }
        }

        public IReadOnlyList<string> ProbesStillOpen()
        {
            lock (gate)
            {
                return [.. probing.Where(tuner => tuner.Value > 0).Select(tuner => tuner.Key)];
            }
        }

        private void ProbeClosed(string deviceId)
        {
            lock (gate)
            {
                probing[deviceId] = probing.GetValueOrDefault(deviceId) - 1;
            }
        }

        private sealed class Probe(DevicesUnderTest owner, string deviceId) : IReceptionProbe
        {
            private static readonly FakeTunerDevice Signal = new(20);

            private int closed;

            public SignalQuality Measure()
            {
                if (Volatile.Read(ref closed) is 1)
                {
                    owner.Report($"{deviceId} was read for a measurement after it was closed");
                }

                if (owner.random.Value!.Next(400) is 0)
                {
                    throw new IOException($"{deviceId} stopped answering while its signal was read.");
                }

                return Signal.Measure();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) is 1)
                {
                    owner.Report($"the measurement of {deviceId} was closed twice");

                    return;
                }

                owner.ProbeClosed(deviceId);
            }
        }

        private void BeginOpening(string deviceId, string how)
        {
            lock (gate)
            {
                int handles = open.GetValueOrDefault(deviceId) + probing.GetValueOrDefault(deviceId);
                int others = opening.GetValueOrDefault(deviceId);

                if (handles > 0 || others > 0)
                {
                    violations.Enqueue($"{deviceId} was {how} while {handles} of it was open and {others} other opens were under way");
                }

                opening[deviceId] = others + 1;
            }
        }

        private void EndOpening(string deviceId, Device? opened)
        {
            lock (gate)
            {
                opening[deviceId] = opening.GetValueOrDefault(deviceId) - 1;

                if (opened is not null)
                {
                    open[deviceId] = open.GetValueOrDefault(deviceId) + 1;
                    created.Add(opened);
                }
            }
        }

        public void FailOne(Random pick)
        {
            Device[] live;

            lock (gate)
            {
                live = [.. created.Where(device => !device.Closed)];
            }

            if (live.Length > 0)
            {
                live[pick.Next(live.Length)].Fail();
            }
        }

        public int OpenOn(string deviceId)
        {
            lock (gate)
            {
                return open.GetValueOrDefault(deviceId);
            }
        }

        public IReadOnlyList<string> StillOpen()
        {
            lock (gate)
            {
                return [.. created.Where(device => !device.Closed).Select(device => device.DeviceId)];
            }
        }

        public void Release()
        {
            random.Dispose();
        }

        private void Report(string violation) => violations.Enqueue(violation);

        private void Closed(Device device)
        {
            lock (gate)
            {
                open[device.DeviceId] = open.GetValueOrDefault(device.DeviceId) - 1;
            }
        }

        private sealed class Device(DevicesUnderTest owner, string deviceId, int channel) : ITunerDevice
        {
            private readonly FakeTunerDevice inner = new(channel);
            private int closed;
            private int failing;

            public string DeviceId { get; } = deviceId;

            public bool Closed => Volatile.Read(ref closed) is 1;

            public long Overflows => 0;

            public void Fail() => Volatile.Write(ref failing, 1);

            public byte[] Read(int count, CancellationToken cancellationToken)
            {
                Thread.Sleep(1);
                cancellationToken.ThrowIfCancellationRequested();

                if (Volatile.Read(ref failing) is 1 || Closed)
                {
                    throw new IOException($"{DeviceId} stopped answering.");
                }

                return inner.Read(count, cancellationToken);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) is 1)
                {
                    owner.Report($"{DeviceId} was closed twice");

                    return;
                }

                owner.Closed(this);
            }
        }
    }

    private sealed class DiscardingWriters : IRecordingWriterFactory
    {
        public IRecordingWriter Open(string recordingsDirectory, string recordingId) => new Discarding(recordingId);

        private sealed class Discarding(string recordingId) : IRecordingWriter
        {
            private long written;

            public string Path { get; } = recordingId;

            public long BytesWritten => Interlocked.Read(ref written);

            public void Write(ReadOnlySpan<byte> bytes) => Interlocked.Add(ref written, bytes.Length);

            public void Dispose() { }
        }
    }

    private sealed class AcceleratedClock(int factor) : TimeProvider
    {
        private readonly DateTimeOffset origin = DateTimeOffset.UtcNow;
        private readonly long started = System.GetTimestamp();

        public override DateTimeOffset GetUtcNow() =>
            origin + (System.GetElapsedTime(started) * factor);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, Faster(dueTime), Faster(period));

        private TimeSpan Faster(TimeSpan span) =>
            span == Timeout.InfiniteTimeSpan || span <= TimeSpan.Zero ? span : span / factor;
    }

    private sealed class MeasurerLog : ILogger<IdleReceptionMeasurer>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        { }
    }

    private sealed class RefusalLog : ILogger<TunerSessionManager>
    {
        public ConcurrentQueue<string> Refusals { get; } = new();

        public ConcurrentQueue<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            string message = formatter(state, exception);

            if (message.Contains("cannot take", StringComparison.Ordinal))
            {
                Refusals.Enqueue(message);
            }

            if (logLevel >= LogLevel.Error)
            {
                Errors.Enqueue(message);
            }
        }
    }
}
