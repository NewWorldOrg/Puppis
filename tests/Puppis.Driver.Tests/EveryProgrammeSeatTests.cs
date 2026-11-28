using System.Collections.Concurrent;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Transport;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class EveryProgrammeSeatTests : IDisposable
{
    private const int Channel = 55;

    private const int Service = 1;

    private const int OtherService = 2;

    private const int PmtPid = 0x0101;

    private const int OtherPmtPid = 0x0102;

    private const int EcmPid = 0x0901;

    private const int OtherEcmPid = 0x0902;

    private const int VideoPid = 0x0111;

    private const int OtherVideoPid = 0x0121;

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly byte[] FirstEcm = Encoding.ASCII.GetBytes("synthetic ecm one");

    private static readonly byte[] SecondEcm = Encoding.ASCII.GetBytes("second synthetic ecm");

    private readonly string root = Directory.CreateTempSubdirectory("puppis-every-programme-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void ASeatThatWaitsForEveryProgrammeIsHandedOnlyWhatCarriesEveryProgramme()
    {
        using var broadcaster = new SessionBroadcaster();
        SessionSubscription viewer = broadcaster.Subscribe(SubscriberKind.Viewer);
        SessionSubscription recording = broadcaster.Subscribe(SubscriberKind.Recording, waitsForEveryProgramme: true);

        broadcaster.Publish([1], carriesEveryProgramme: false);
        broadcaster.Publish([2], carriesEveryProgramme: true);

        Assert.Equal([[1], [2]], Drained(viewer));
        Assert.Equal([[2]], Drained(recording));
    }

    [Fact]
    public void AReadIsCutWhereEveryProgrammeBecomesReadableAndTheRestIsHandedOnByTheNextRead()
    {
        var source = new ChunkByChunkTunerDevice([[1, 2, 3, 4], [5]]);
        var descrambler = new SplittingDescrambler([2, 0]);

        using var device = new DescramblingTunerDevice(source, descrambler, new CardsOnRequest(() => null));

        Assert.Equal([1, 2], device.Read(4, CancellationToken.None));
        Assert.False(device.CarriesEveryProgramme);
        Assert.Equal([3, 4], device.Read(4, CancellationToken.None));
        Assert.True(device.CarriesEveryProgramme);
        Assert.Equal([5], device.Read(4, CancellationToken.None));
        Assert.True(device.CarriesEveryProgramme);
    }

    [Fact]
    public void AReadWithNothingReadableByEveryProgrammeSaysSo()
    {
        var source = new ChunkByChunkTunerDevice([[1, 2]]);

        using var device = new DescramblingTunerDevice(source, new SplittingDescrambler([2]), new CardsOnRequest(() => null));

        Assert.Equal([1, 2], device.Read(4, CancellationToken.None));
        Assert.False(device.CarriesEveryProgramme);
    }

    [Fact]
    public async Task ACardAskedForPartWayThroughTheStreamPutsNoTablesInFrontAndNoCounterJumps()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Ecm(EcmPid, FirstEcm);
        byte[] first = stream.Input;
        stream.LeftScrambled(VideoPid, FirstEcm, odd: false, seed: 1);
        byte[] second = stream.Input[first.Length..];
        stream
            .Pat((Service, PmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: true, seed: 2)
            .Ecm(EcmPid, FirstEcm)
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 3);
        byte[] third = stream.Input[(first.Length + second.Length)..];
        var source = new ChunkByChunkTunerDevice([first, second, third]);
        var cards = new HoldKeepingCards();
        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, Channel, Service), new ServiceTableMemory());

        using var device = new DescramblingTunerDevice(source, descrambler: null, cards, clock, hold: hold);

        List<byte> output = [.. device.Read(first.Length, CancellationToken.None)];
        clock.Advance(DescramblingTunerDevice.BetweenAsksForTheCard);
        output.AddRange(device.Read(second.Length, CancellationToken.None));
        await device.AskedForTheCard;
        output.AddRange(device.Read(third.Length, CancellationToken.None));

        Assert.False(Assert.Single(cards.Holds).Service!.PutsTablesInFront);
        Assert.Equal(stream.Output, output);
        SessionCounters counted = Counted([.. output]);
        Assert.Equal(0, counted.Drops);
        Assert.Equal(0, counted.Duplicates);
    }

    [Fact]
    public async Task ARecordingThatJoinsALiveServiceDuringItsHoldIsHandedNoScrambledPacket()
    {
        GatedTunerDevice source = new();
        TunerSessionManager manager = Manager(source);

        TunerSession live = Started(manager, Request("live-1", SessionPurpose.Live));
        Assert.True(source.Reading.Wait(Deadlock));
        IReadOnlyList<byte[]> parts = Parts();
        Task<TunerSession> joining = Task.Run(() => Started(manager, Request("rec-1", SessionPurpose.Recording)));
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        source.Feed(parts[0]);
        TunerSession recording = await joining.WaitAsync(Deadlock);

        foreach (byte[] part in parts.Skip(1))
        {
            source.Feed(part);
        }

        AssertTheRecordingHadNoScrambledPacket(live, recording);
    }

    [Fact]
    public void ARecordingThatJoinsALiveServiceJustAfterItsHeadWasHandedOnIsHandedNoScrambledPacket()
    {
        GatedTunerDevice source = new();
        TunerSessionManager manager = Manager(source);
        IReadOnlyList<byte[]> parts = Parts();

        TunerSession live = Started(manager, Request("live-1", SessionPurpose.Live));
        source.Feed(parts[0]);
        Assert.True(SpinWait.SpinUntil(() => live.Counters.Snapshot().Packets > 0, Deadlock));
        TunerSession recording = Started(manager, Request("rec-1", SessionPurpose.Recording));

        foreach (byte[] part in parts.Skip(1))
        {
            source.Feed(part);
        }

        AssertTheRecordingHadNoScrambledPacket(live, recording);
    }

    [Fact]
    public void ARecordingReadingATunerDirectlyCountsNothingThatDoesNotCarryEveryProgrammeAndALiveSessionStillDoes()
    {
        SessionCounters recorded = CountedBy(SessionPurpose.Recording);
        SessionCounters watched = CountedBy(SessionPurpose.Live);

        Assert.Equal(0, recorded.ScrambledPackets);
        Assert.True(recorded.Packets > 0);
        Assert.True(watched.ScrambledPackets > 0);
    }

    private SessionCounters CountedBy(SessionPurpose purpose)
    {
        SyntheticScrambledStream scrambled = new();
        SyntheticScrambledStream clear = new();
        for (int seed = 0; seed < 8; seed++)
        {
            scrambled.LeftScrambled(OtherVideoPid, SecondEcm, odd: false, seed: seed);
            clear.Clear(VideoPid, seed);
        }

        FlaggedTunerDevice device = new([(scrambled.Input, false), (clear.Input, true)]);
        TunerSessionManager manager = new(
            Configuration,
            new OneTunerDeviceFactory(device),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance
        );

        TunerSession session = Started(manager, Request("s-1", purpose));
        Assert.True(SpinWait.SpinUntil(() => device.Finished, Deadlock));
        Assert.True(SpinWait.SpinUntil(() => session.Counters.Snapshot().Packets > 0, Deadlock));
        session.Stop();
        Assert.True(session.Completion.Wait(Deadlock));

        return session.Counters.Snapshot();
    }

    private static void AssertTheRecordingHadNoScrambledPacket(TunerSession live, TunerSession recording)
    {
        Assert.True(SpinWait.SpinUntil(() => recording.Counters.Snapshot().Packets >= 2, Deadlock));
        recording.Stop();
        live.Stop();
        Assert.True(recording.Completion.Wait(Deadlock));
        Assert.True(live.Completion.Wait(Deadlock));

        Assert.Equal(0, recording.Counters.Snapshot().ScrambledPackets);
        Assert.True(live.Counters.Snapshot().ScrambledPackets > 0);
    }

    private static IReadOnlyList<byte[]> Parts()
    {
        SyntheticScrambledStream stream = new SyntheticScrambledStream()
            .Pat((Service, PmtPid), (OtherService, OtherPmtPid))
            .Pmt(PmtPid, Service, EcmPid, [(VideoPid, null)])
            .Scrambled(VideoPid, FirstEcm, odd: false, seed: 1)
            .LeftScrambled(OtherVideoPid, SecondEcm, odd: false, seed: 2)
            .Ecm(EcmPid, FirstEcm);
        List<int> cuts = [stream.PacketCount];
        stream.LeftScrambled(OtherVideoPid, SecondEcm, odd: true, seed: 3).Scrambled(VideoPid, FirstEcm, odd: true, seed: 4);
        cuts.Add(stream.PacketCount);
        stream.Pmt(OtherPmtPid, OtherService, OtherEcmPid, [(OtherVideoPid, null)]).Ecm(OtherEcmPid, SecondEcm);
        cuts.Add(stream.PacketCount);
        stream.Scrambled(OtherVideoPid, SecondEcm, odd: true, seed: 5).Scrambled(VideoPid, FirstEcm, odd: false, seed: 6);
        cuts.Add(stream.PacketCount);

        byte[] input = stream.Input;
        int from = 0;
        List<byte[]> parts = [];
        foreach (int cut in cuts)
        {
            parts.Add(input[(from * SyntheticScrambledStream.PacketLength)..(cut * SyntheticScrambledStream.PacketLength)]);
            from = cut;
        }

        return parts;
    }

    private DriverConfiguration Configuration =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", root)],
            6,
            new TunerSettings(TunerBackend.Fake),
            [new DeviceSettings("adapter0", DeviceKind.Terrestrial)]
        );

    private TunerSessionManager Manager(GatedTunerDevice source) =>
        new(
            Configuration,
            new DescramblingFactory(source),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance
        );

    private static StartSessionRequest Request(string sessionId, SessionPurpose purpose) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = purpose,
            Tuning = new TuningRequest(TunerKind.Terrestrial, Channel, Service),
            DeviceId = "adapter0",
            OutputRoot = purpose is SessionPurpose.Recording ? "primary" : null,
            RecordingId = purpose is SessionPurpose.Recording ? $"k-{sessionId}" : null,
            EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        };

    private static TunerSession Started(TunerSessionManager manager, StartSessionRequest request)
    {
        SessionStart start = manager.Begin(request);

        Assert.True(start.TryGetSession(out TunerSession? session), start.Detail);

        return session;
    }

    private static List<byte[]> Drained(SessionSubscription subscription)
    {
        List<byte[]> chunks = [];
        while (subscription.Reader.TryRead(out byte[]? chunk))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static SessionCounters Counted(byte[] output)
    {
        TsPacketReader reader = new();
        ContinuityCounterTracker tracker = new();
        foreach (TsPacket packet in reader.Read(output))
        {
            tracker.Observe(packet);
        }

        return tracker.Snapshot();
    }

    private static TransportStreamDescrambler Descrambler(HeadHold hold) =>
        new(ConditionalAccessCard.Open(FakeSmartCardService.WithOneCard(out _)), hold: hold);

    private sealed class DescramblingFactory(GatedTunerDevice source) : ITunerDeviceFactory
    {
        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold) =>
            new DescramblingTunerDevice(source, Descrambler(hold), new HoldKeepingCards(), hold: hold);
    }

    private sealed class HoldKeepingCards : IDescramblerFactory
    {
        public List<HeadHold> Holds { get; } = [];

        public bool Unscrambles => true;

        public CardIdentity? CardInHand => null;

        public IDescrambler? Open(HeadHold hold)
        {
            Holds.Add(hold);

            return Descrambler(hold);
        }
    }

    private sealed class GatedTunerDevice : ITunerDevice
    {
        private static readonly byte[] Filler = [0x47, 0x1F, 0xFF, 0x10, .. Enumerable.Repeat((byte)0xFF, 184)];

        private readonly BlockingCollection<byte[]> parts = [];

        public ManualResetEventSlim Reading { get; } = new(false);

        public long Overflows => 0;

        public void Feed(byte[] part) => parts.Add(part);

        public byte[] Read(int count, CancellationToken cancellationToken)
        {
            Reading.Set();

            return parts.TryTake(out byte[]? part, 20, cancellationToken) ? part : Filler;
        }

        public void Dispose() { }
    }

    private sealed class FlaggedTunerDevice(IReadOnlyList<(byte[] Bytes, bool EveryProgramme)> reads) : ITunerDevice
    {
        private static readonly byte[] Filler = [0x47, 0x1F, 0xFF, 0x10, .. Enumerable.Repeat((byte)0xFF, 184)];

        private int next;

        public long Overflows => 0;

        public bool CarriesEveryProgramme { get; private set; } = true;

        public bool Finished => Volatile.Read(ref next) >= reads.Count;

        public byte[] Read(int count, CancellationToken cancellationToken)
        {
            if (next >= reads.Count)
            {
                Thread.Sleep(10);
                CarriesEveryProgramme = true;

                return Filler;
            }

            (byte[] bytes, bool everyProgramme) = reads[next];
            CarriesEveryProgramme = everyProgramme;
            Interlocked.Increment(ref next);

            return bytes;
        }

        public void Dispose() { }
    }

    private sealed class SplittingDescrambler(IReadOnlyList<int> froms) : IDescrambler
    {
        private int reads;

        public int EveryProgrammeFrom { get; private set; }

        public byte[] Descramble(ReadOnlySpan<byte> stream)
        {
            EveryProgrammeFrom = froms[Math.Min(reads++, froms.Count - 1)];

            return stream.ToArray();
        }

        public byte[] WhatItCouldNotRead() => [];

        public void Dispose() { }
    }
}
