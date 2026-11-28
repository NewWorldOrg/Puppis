using Microsoft.Extensions.Logging;

namespace Puppis.Driver.Descrambling;

public sealed class TransportStreamDescrambler : IDescrambler
{
    public const int PacketLength = 188;

    public const int LongestHold = 8 * 1024 * 1024;

    public const byte EcmTableId = 0x82;

    private const byte SyncByte = 0x47;

    private const int PatPid = 0x0000;

    private const int HeaderLength = 4;

    private const int EvenKey = 2;

    private const int OddKey = 3;

    private const int MostPidsNamed = 16;

    private const string LiveCondition = "live";

    private const string EveryProgrammeCondition = "every-programme";

    private const string ServiceCondition = "service";

    private const byte PayloadPresent = 0x10;

    private readonly IConditionalAccess card;

    private readonly ILogger? logger;

    private readonly int holdLimit;

    private readonly HeadHold hold;

    private readonly LiveService? service;

    private readonly ServiceTables? remembered;

    private readonly HashSet<int> rememberedEcms = [];

    private readonly TimeProvider time;

    private readonly string cardKind;

    private readonly HeadHoldTimes head;

    private readonly ProgramMap map;

    private readonly Lock gate = new();

    private readonly Dictionary<int, SectionCollector> collectors = [];

    private readonly Dictionary<int, EcmState> ecms = [];

    private readonly ByteShelf shelf = new();

    private readonly List<int> waiting = [];

    private byte[] partial = [];

    private bool holding = true;

    private bool lookingBack;

    private bool headReported;

    private HeadReport? headToReport;

    private bool memoryToSettle;

    private bool everyProgrammeReadable;

    private int? everyProgrammeFrom;

    private long bytesTaken;

    private bool disposed;

    public TransportStreamDescrambler(
        IConditionalAccess card,
        ILogger? logger = null,
        int holdLimit = LongestHold,
        HeadHold? hold = null,
        TimeProvider? time = null,
        string cardKind = "local"
    )
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentOutOfRangeException.ThrowIfLessThan(holdLimit, PacketLength);

        this.card = card;
        this.logger = logger;
        this.holdLimit = holdLimit;
        this.hold = hold ?? HeadHold.EveryProgramme;
        this.time = time ?? TimeProvider.System;
        this.cardKind = cardKind;
        head = new HeadHoldTimes(this.time);
        map = new ProgramMap(card.CaSystemId);
        service = this.hold.Service;
        remembered = service?.Memory.Recall(service.Key);
        rememberedEcms = [.. remembered?.EcmPids ?? []];
    }

    public int EveryProgrammeFrom { get; private set; }

    public byte[] Descramble(ReadOnlySpan<byte> stream)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (holding && stream.Length > 0)
            {
                head.BytesCame();
            }

            int shelved = shelf.Length;
            byte[] carried = partial;
            bool wasHolding = holding;
            bool wasReported = headReported;
            bool wasReadable = everyProgrammeReadable;
            long taken = bytesTaken;
            byte[] readable;

            try
            {
                readable = Take(stream);
            }
            catch
            {
                shelf.Truncate(shelved);
                waiting.RemoveAll(offset => offset >= shelved);
                partial = carried;
                holding = wasHolding;
                headReported = wasReported;
                headToReport = null;
                memoryToSettle = false;
                everyProgrammeReadable = wasReadable;
                bytesTaken = taken;

                throw;
            }

            WriteTheHeadReport();
            SettleTheMemory();

            return readable;
        }
    }

    public byte[] WhatItCouldNotRead()
    {
        lock (gate)
        {
            ReportAHeadStillHeld("what it held was taken back unread");
            byte[] unread = [.. shelf.Written, .. partial];
            shelf.Clear();
            waiting.Clear();
            partial = [];

            return unread;
        }
    }

    private byte[] Take(ReadOnlySpan<byte> stream)
    {
        byte[] work = [.. partial, .. stream];
        ByteShelf output = new();
        bool released = false;
        int position = NextAlignment(work, 0);
        everyProgrammeFrom = everyProgrammeReadable ? 0 : null;

        while (position + PacketLength <= work.Length)
        {
            Span<byte> packet = work.AsSpan(position, PacketLength);
            bytesTaken += PacketLength;
            Unscramble(packet, out bool unkeyed);
            released |= Pass(packet, unkeyed, output);
            position = NextAlignment(work, position + PacketLength);
        }

        partial = work[position..];
        EveryProgrammeFrom = everyProgrammeFrom ?? output.Length;

        if (released)
        {
            shelf.Clear();
            waiting.Clear();
        }

        return output.ToArray();
    }

    private bool Pass(Span<byte> packet, bool unkeyed, ByteShelf output)
    {
        if (!holding)
        {
            NoteWhetherEveryProgrammeIsReadable(output);
            output.Append(packet);

            return false;
        }

        int offset = shelf.Append(packet);
        if (unkeyed)
        {
            waiting.Add(offset);
        }

        string? met = ConditionMet();

        if (met is null && shelf.Length < holdLimit)
        {
            return false;
        }

        if (met is null)
        {
            logger?.LogWarning(
                "The head of the stream was held for {Bytes} bytes without every programme and ECM being read, so it is handed on as it stands.",
                shelf.Length
            );
        }

        byte[] tables = TablesInFront();
        ReportTheHead(met, tables.Length / PacketLength);
        memoryToSettle = service is not null;
        holding = false;
        NoteWhetherEveryProgrammeIsReadable(output, met is not ServiceCondition);
        output.Append(tables);
        output.Append(shelf.Written);

        return true;
    }

    private void NoteWhetherEveryProgrammeIsReadable(ByteShelf output, bool letGoForEveryProgramme = false)
    {
        if (everyProgrammeReadable || !(letGoForEveryProgramme || EveryProgrammeIsSettled() || bytesTaken >= holdLimit))
        {
            return;
        }

        everyProgrammeReadable = true;
        everyProgrammeFrom = output.Length;
    }

    private byte[] TablesInFront()
    {
        if (service is not { PutsTablesInFront: true })
        {
            return [];
        }

        int id = service.Key.ServiceId;
        byte[] pat = map.PatSeen ? TableHead.Packets(PatPid, map.PatSections, FirstCounterOn(PatPid)) : [];
        byte[] pmt = map.PmtOf(id) is { } section && map.PmtPidOf(id) is int pmtPid
            ? TableHead.Packets(pmtPid, [section], FirstCounterOn(pmtPid))
            : [];

        return [.. pat, .. pmt];
    }

    private int? FirstCounterOn(int pid)
    {
        for (int offset = 0; offset < shelf.Length; offset += PacketLength)
        {
            ReadOnlySpan<byte> packet = shelf.At(offset, PacketLength);
            bool carriesAPayload = (packet[1] & 0x80) is 0 && (packet[3] & PayloadPresent) is not 0;

            if (carriesAPayload && PidOf(packet) == pid)
            {
                return packet[3] & 0x0F;
            }
        }

        return null;
    }

    private static int NextAlignment(byte[] work, int from)
    {
        for (int position = from; position < work.Length; position++)
        {
            if (work[position] is not SyncByte)
            {
                continue;
            }

            int next = position + PacketLength;
            if (next >= work.Length || work[next] is SyncByte)
            {
                return position;
            }
        }

        return Math.Max(from, work.Length - (PacketLength - 1));
    }

    private void Unscramble(Span<byte> packet, out bool unkeyed)
    {
        unkeyed = false;

        if ((packet[1] & 0x80) is not 0)
        {
            return;
        }

        int pid = PidOf(packet);
        int scrambling = packet[3] >> 6;
        int adaptation = (packet[3] >> 4) & 0x03;
        int start = PayloadStart(packet, adaptation);

        if (start < 0)
        {
            return;
        }

        if (scrambling is 0)
        {
            Read(pid, packet, start, adaptation);

            return;
        }

        if (start == PacketLength)
        {
            packet[3] &= 0x3F;

            return;
        }

        unkeyed = !TryUnscramble(packet, pid, start) && scrambling is EvenKey or OddKey;
    }

    private static int PidOf(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    private static int PayloadStart(ReadOnlySpan<byte> packet, int adaptation)
    {
        if (adaptation is 0x01)
        {
            return HeaderLength;
        }

        if (adaptation is 0x00)
        {
            return -1;
        }

        int start = HeaderLength + 1 + packet[HeaderLength];
        if (start > PacketLength || (adaptation is 0x03 && start == PacketLength))
        {
            return -1;
        }

        return adaptation is 0x02 ? PacketLength : start;
    }

    private bool TryUnscramble(Span<byte> packet, int pid, int start)
    {
        int scrambling = packet[3] >> 6;
        if (scrambling is not (EvenKey or OddKey))
        {
            return false;
        }

        ScrambleKeys? keys = KeysFor(pid);
        if (keys is null)
        {
            return false;
        }

        keys.Unscramble(packet[start..], withOddKey: scrambling is OddKey);
        packet[3] &= 0x3F;

        return true;
    }

    private ScrambleKeys? KeysFor(int pid)
    {
        int? ecm = map.EcmFor(pid);

        return ecm is int named && ecms.TryGetValue(named, out EcmState? state) ? state.Keys : null;
    }

    private void Read(int pid, ReadOnlySpan<byte> packet, int start, int adaptation)
    {
        bool section = pid is PatPid || map.IsPmt(pid) || IsEcm(pid);
        if (!section || adaptation is 0x02)
        {
            return;
        }

        if (!collectors.TryGetValue(pid, out SectionCollector? collector))
        {
            collector = new SectionCollector();
            collectors[pid] = collector;
        }

        Collect(collector, packet, start, whole => Whole(pid, whole));
    }

    private static void Collect(
        SectionCollector collector,
        ReadOnlySpan<byte> packet,
        int start,
        Action<ReadOnlySpan<byte>> whole
    )
    {
        bool unitStart = (packet[1] & 0x40) is not 0;
        collector.Take(packet[start..], unitStart, packet[3] & 0x0F, whole);
    }

    private void Whole(int pid, ReadOnlySpan<byte> section)
    {
        if (pid is PatPid)
        {
            map.ReadPat(section);
            ForgetWhatIsNoLongerRead();
            NoteThePat();
        }
        else if (map.IsPmt(pid))
        {
            map.ReadPmt(section);
            NoteThePmt();
        }
        else if (section[0] is EcmTableId && IsEcm(pid))
        {
            Ask(pid, PsiSection.Body(section));
        }

        if (holding)
        {
            LookBack();
            UnscrambleWhatWaited();
        }
    }

    private void ForgetWhatIsNoLongerRead()
    {
        foreach (int pid in collectors.Keys.Where(pid => pid is not PatPid && !map.IsPmt(pid) && !IsEcm(pid)).ToList())
        {
            collectors.Remove(pid);
        }
    }

    private bool IsEcm(int pid) => map.IsEcm(pid) || (holding && rememberedEcms.Contains(pid));

    private void LookBack()
    {
        if (lookingBack)
        {
            return;
        }

        lookingBack = true;

        try
        {
            while (UnreadSectionPid() is int pid)
            {
                LookBackOn(pid);
            }
        }
        finally
        {
            lookingBack = false;
        }
    }

    private int? UnreadSectionPid() =>
        map.PmtPids.Concat(map.EcmPids).Where(pid => !collectors.ContainsKey(pid)).Cast<int?>().FirstOrDefault();

    private void LookBackOn(int pid)
    {
        SectionCollector collector = new();
        collectors[pid] = collector;

        bool pmt = map.IsPmt(pid);
        byte[]? latestEcm = null;

        for (int offset = 0; offset < shelf.Length; offset += PacketLength)
        {
            ReadOnlySpan<byte> packet = shelf.At(offset, PacketLength);
            int start = SectionStart(packet);
            if (PidOf(packet) != pid || start < 0)
            {
                continue;
            }

            Collect(collector, packet, start, whole =>
            {
                if (pmt)
                {
                    Whole(pid, whole);
                }
                else if (whole[0] is EcmTableId)
                {
                    latestEcm = whole.ToArray();
                }
            });
        }

        if (latestEcm is not null)
        {
            Ask(pid, PsiSection.Body(latestEcm));
        }
    }

    private static int SectionStart(ReadOnlySpan<byte> packet)
    {
        int adaptation = (packet[3] >> 4) & 0x03;
        bool damaged = (packet[1] & 0x80) is not 0;
        bool scrambled = (packet[3] >> 6) is not 0;

        return damaged || scrambled || adaptation is 0x02 ? -1 : PayloadStart(packet, adaptation);
    }

    private void NoteThePat()
    {
        if (map.PatSeen)
        {
            head.PatSeen();
        }
    }

    private void NoteThePmt()
    {
        if (map.EcmPids.Count > 0)
        {
            head.PmtNamingAnEcmSeen();
        }

        if (service is not null && map.PmtOf(service.Key.ServiceId) is not null)
        {
            head.ServicePmtSeen();
        }
    }

    private void Ask(int pid, ReadOnlySpan<byte> body)
    {
        head.EcmSeen();

        if (!ecms.TryGetValue(pid, out EcmState? state))
        {
            state = new EcmState();
            ecms[pid] = state;
        }

        state.Seen++;

        if (state.Answered && body.SequenceEqual(state.LastBody))
        {
            return;
        }

        state.LastBody = body.ToArray();
        state.Answered = true;

        if (body.Length is 0 or > ConditionalAccessCard.LongestEcmBody)
        {
            state.Keys = null;

            return;
        }

        state.Asks++;
        bool first = head.Answer is null;
        long asked = first ? time.GetTimestamp() : 0;
        EcmAnswer answer = card.Answer(body);
        state.Answers++;
        state.Keys = answer.Keys;

        if (first)
        {
            ReportTheFirstAnswer(pid, time.GetElapsedTime(asked));
        }

        if (answer.Granted)
        {
            state.LastRefusal = null;

            return;
        }

        if (state.LastRefusal != answer.ReturnCode)
        {
            state.LastRefusal = answer.ReturnCode;
            logger?.LogWarning(
                "The card would not unlock the ECM on PID 0x{Pid:X4} (return code 0x{Code:X4}), so what it covers stays scrambled until the ECM changes.",
                pid,
                answer.ReturnCode
            );
        }
    }

    private void UnscrambleWhatWaited()
    {
        waiting.RemoveAll(offset =>
        {
            Span<byte> packet = shelf.At(offset, PacketLength);
            int adaptation = (packet[3] >> 4) & 0x03;

            return TryUnscramble(packet, PidOf(packet), PayloadStart(packet, adaptation));
        });
    }

    private void ReportTheFirstAnswer(int pid, TimeSpan took)
    {
        head.Answered();
        logger?.LogInformation(
            "The first ECM, on PID 0x{Pid:X4}, was answered by the {Card} card in {Took:0.0} ms, {Since:0.0} ms after the first byte was received.",
            pid,
            cardKind,
            took.TotalMilliseconds,
            head.SinceTheFirstByte.TotalMilliseconds
        );
    }

    private string HeadTimes => service is null ? head.ToString() : head.WithTheServicePmt();

    private string HoldName => (hold.Kind, service) switch
    {
        (_, { } watched) => $"{ServiceCondition} {watched.Key.ServiceId}",
        (HeadHoldKind.WhatIsHeld, _) => LiveCondition,
        _ => EveryProgrammeCondition,
    };

    private string? ConditionMet()
    {
        if (service is not null && !TheServiceIsGone(service))
        {
            return TheServiceCanBeRead(service) ? ServiceCondition : null;
        }

        if (EveryProgrammeIsSettled())
        {
            return EveryProgrammeCondition;
        }

        return EverythingHeldCanBeRead() ? LiveCondition : null;
    }

    private bool TheServiceIsGone(LiveService watched) => map.PatSeen && !map.Lists(watched.Key.ServiceId);

    private bool TheServiceCanBeRead(LiveService watched)
    {
        int id = watched.Key.ServiceId;
        if (map.PmtOf(id) is null || !Answered(map.EcmPidsOf(id)))
        {
            return false;
        }

        HashSet<int> carried = [.. map.StreamPidsOf(id)];

        return HeldOn(carried).All(pid => Answered(map.EcmFor(pid)));
    }

    private IEnumerable<int> HeldOn(HashSet<int> pids) =>
        waiting.Select(offset => PidOf(shelf.At(offset, PacketLength))).Where(pids.Contains);

    private void ReportTheHead(string? met, int tablePackets)
    {
        if (headReported || logger is null)
        {
            return;
        }

        headReported = true;
        headToReport = new HeadReport(
            shelf.Length,
            head.SinceTheFirstByte.TotalMilliseconds,
            met,
            met is null ? WhatIsStillMissing() : string.Empty,
            HeadTimes,
            tablePackets
        );
    }

    private void WriteTheHeadReport()
    {
        if (headToReport is not { } report || logger is null)
        {
            return;
        }

        headToReport = null;

        if (report.Met is null)
        {
            logger.LogWarning(
                "The head of the stream reached its size limit under the {Hold} hold, {Milliseconds:0.0} ms after the first byte was received, with this still missing: {Missing}.",
                HoldName,
                report.HeldMilliseconds,
                report.Missing
            );
        }

        string why = report.Met is null ? "it reached its size limit" : $"the {report.Met} condition was met";

        if (service is null)
        {
            logger.LogInformation(
                "The head of the stream, {Bytes} bytes, was handed on {Milliseconds:0.0} ms after the first byte was received under the {Hold} hold because {Why}: {Times}.",
                report.Bytes,
                report.HeldMilliseconds,
                HoldName,
                why,
                report.Times
            );

            return;
        }

        logger.LogInformation(
            "The head of the stream, {Bytes} bytes, was handed on {Milliseconds:0.0} ms after the first byte was received under the {Hold} hold because {Why}, {Memory}, and {TablePackets} packets of the PAT and the service's PMT were put in front of it: {Times}.",
            report.Bytes,
            report.HeldMilliseconds,
            HoldName,
            why,
            remembered is null ? "with no tables remembered from an earlier opening" : "using the tables remembered from an earlier opening",
            report.TablePackets,
            report.Times
        );
    }

    private void SettleTheMemory()
    {
        if (!memoryToSettle || service is null)
        {
            return;
        }

        memoryToSettle = false;
        int id = service.Key.ServiceId;

        if (map.PmtOf(id) is { } pmt && map.PmtPidOf(id) is int pmtPid)
        {
            ServiceTables carried = new(map.PatSections, pmtPid, pmt, map.EcmPidsOf(id));
            service.Memory.Remember(service.Key, carried);
            CompareWithWhatWasRemembered(carried);

            return;
        }

        if (TheServiceIsGone(service))
        {
            service.Memory.Forget(service.Key);
            ReportTheMemory(remembered is null ? null : ["the PAT does not list the service"], "forgotten");
        }
    }

    private void CompareWithWhatWasRemembered(ServiceTables carried)
    {
        if (remembered is not null)
        {
            ReportTheMemory(remembered.DifferencesFrom(carried), "replaced by what it carried");
        }
    }

    private void ReportTheMemory(IReadOnlyList<string>? differences, string outcome)
    {
        if (differences is null || logger is null || service is null)
        {
            return;
        }

        ServiceTablesKey key = service.Key;

        if (differences.Count is 0)
        {
            logger.LogInformation(
                "The tables remembered for service {Service} on {Kind} channel {Channel} matched the PAT and the PMT this stream carried.",
                key.ServiceId,
                key.Kind,
                key.PhysicalChannel
            );

            return;
        }

        logger.LogInformation(
            "The tables remembered for service {Service} on {Kind} channel {Channel} differed from what this stream carried ({Differences}), so they were {Outcome}.",
            key.ServiceId,
            key.Kind,
            key.PhysicalChannel,
            string.Join("; ", differences),
            outcome
        );
    }

    private void ReportAHeadStillHeld(string when)
    {
        if (!holding || headReported || !head.Started || logger is null)
        {
            return;
        }

        headReported = true;
        logger.LogInformation(
            "The head of the stream, {Bytes} bytes, was still held when {When}, {Milliseconds:0.0} ms after the first byte was received and {Idle:0.0} ms after the last, under the {Hold} hold, with this still missing: {Missing}; {Times}.",
            shelf.Length,
            when,
            head.SinceTheFirstByte.TotalMilliseconds,
            head.SinceTheLastBytes.TotalMilliseconds,
            HoldName,
            WhatIsStillMissing(),
            HeadTimes
        );
    }

    private string WhatIsStillMissing() =>
        StillMissing() is { Count: > 0 } missing ? string.Join("; ", missing) : "nothing this driver can name";

    private List<string> StillMissing()
    {
        if (service is null)
        {
            return StillMissingForEveryService();
        }

        if (TheServiceIsGone(service))
        {
            return [$"the PAT does not list service {service.Key.ServiceId}", .. StillMissingForEveryService()];
        }

        return StillMissingFor(service.Key.ServiceId);
    }

    private List<string> StillMissingFor(int id)
    {
        List<string> missing = [];

        if (!map.PatSeen)
        {
            missing.Add("the PAT was not seen");
        }
        else if (map.PmtOf(id) is null)
        {
            missing.Add($"the PMT of service {id} on PID 0x{map.PmtPidOf(id):X4} was not seen");
        }

        missing.AddRange(map.EcmPidsOf(id).Where(TheCardGaveNoAnswerFor).Select(Unanswered));

        List<int> untied = [.. HeldOn([.. map.StreamPidsOf(id)]).Where(pid => !Answered(map.EcmFor(pid)))];
        if (untied.Count > 0)
        {
            missing.Add(
                $"{untied.Count} held scrambled packets of service {id} were on PIDs tied to no answered ECM (PID {Named([.. untied.Distinct().Order()])})"
            );
        }

        return missing;
    }

    private List<string> StillMissingForEveryService()
    {
        List<string> missing = [];

        if (!map.PatSeen)
        {
            missing.Add("the PAT was not seen");
        }

        if (hold.Kind is HeadHoldKind.EveryProgramme && map.PmtPidsNotRead is { Count: > 0 } unread)
        {
            missing.Add($"the PMT on PID {Named(unread)} was not seen");
        }

        if (hold.Kind is HeadHoldKind.WhatIsHeld && map.EcmPids.Count is 0)
        {
            missing.Add("no ECM PID is known");
        }

        missing.AddRange(map.EcmPids.Where(TheCardGaveNoAnswerFor).Order().Select(Unanswered));

        if (hold.Kind is HeadHoldKind.WhatIsHeld && HeldWithoutAnAnsweredEcm() is { Count: > 0 } untied)
        {
            missing.Add(
                $"{untied.Count} held scrambled packets were on PIDs tied to no answered ECM (PID {Named([.. untied.Distinct().Order()])})"
            );
        }

        return missing;
    }

    private bool TheCardGaveNoAnswerFor(int ecm) => !ecms.TryGetValue(ecm, out EcmState? state) || state.Answers is 0;

    private string Unanswered(int ecm)
    {
        if (!ecms.TryGetValue(ecm, out EcmState? state) || state.Seen is 0)
        {
            return $"the ECM on PID 0x{ecm:X4} had no answer (an ECM was not seen on it and the card was asked 0 times)";
        }

        return $"the ECM on PID 0x{ecm:X4} had no answer (an ECM was seen on it {state.Seen} times and the card was asked {state.Asks} times without answering)";
    }

    private List<int> HeldWithoutAnAnsweredEcm() =>
        [
            .. waiting
                .Select(offset => PidOf(shelf.At(offset, PacketLength)))
                .Where(pid => !Answered(map.EcmFor(pid))),
        ];

    private static string Named(IReadOnlyList<int> pids)
    {
        string listed = string.Join(", ", pids.Take(MostPidsNamed).Select(pid => $"0x{pid:X4}"));

        return pids.Count > MostPidsNamed ? $"{listed} and {pids.Count - MostPidsNamed} more" : listed;
    }

    private bool EveryProgrammeIsSettled() => map.Complete && Answered(map.EcmPids);

    private bool EverythingHeldCanBeRead() =>
        hold.Kind is HeadHoldKind.WhatIsHeld
        && map.PatSeen
        && map.EcmPids.Count > 0
        && Answered(map.EcmPids)
        && waiting.All(offset => Answered(map.EcmFor(PidOf(shelf.At(offset, PacketLength)))));

    private bool Answered(int? ecm) => ecm is int named && Answered([named]);

    private bool Answered(IEnumerable<int> ecmPids) =>
        ecmPids.All(ecm => ecms.TryGetValue(ecm, out EcmState? state) && state.Answered);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ReportAHeadStillHeld("the descrambler was closed");
        }

        card.Dispose();
    }

    private sealed record HeadReport(int Bytes, double HeldMilliseconds, string? Met, string Missing, string Times, int TablePackets);

    private sealed class EcmState
    {
        public byte[] LastBody { get; set; } = [];

        public bool Answered { get; set; }

        public int Seen { get; set; }

        public int Asks { get; set; }

        public int Answers { get; set; }

        public ScrambleKeys? Keys { get; set; }

        public ushort? LastRefusal { get; set; }
    }

    private sealed class ByteShelf
    {
        private byte[] bytes = new byte[64 * 1024];

        public int Length { get; private set; }

        public ReadOnlySpan<byte> Written => bytes.AsSpan(0, Length);

        public int Append(ReadOnlySpan<byte> more)
        {
            if (Length + more.Length > bytes.Length)
            {
                Array.Resize(ref bytes, Math.Max(bytes.Length * 2, Length + more.Length));
            }

            int offset = Length;
            more.CopyTo(bytes.AsSpan(Length));
            Length += more.Length;

            return offset;
        }

        public Span<byte> At(int offset, int length) => bytes.AsSpan(offset, length);

        public void Truncate(int length) => Length = Math.Min(Length, length);

        public void Clear() => Length = 0;

        public byte[] ToArray() => Written.ToArray();
    }
}
