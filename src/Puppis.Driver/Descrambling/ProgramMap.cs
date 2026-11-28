using System.Buffers.Binary;

namespace Puppis.Driver.Descrambling;

public sealed class ProgramMap(int caSystemId)
{
    public const byte PatTableId = 0x00;

    public const byte PmtTableId = 0x02;

    private const byte CaDescriptorTag = 0x09;

    private const int NoPid = 0x1FFF;

    private readonly Dictionary<int, Dictionary<int, int>> patSections = [];

    private readonly Dictionary<int, IReadOnlyList<(int Pid, int? Ecm)>> programs = [];

    private readonly Dictionary<int, int?> programEcms = [];

    private readonly Dictionary<int, byte[]> patBytes = [];

    private readonly Dictionary<int, byte[]> pmtBytes = [];

    private Dictionary<int, IReadOnlyList<int>> ecmsByProgramme = [];

    private int patVersion = -1;

    private Dictionary<int, int> pmtPids = [];

    private Dictionary<int, int> ecmByPid = [];

    private HashSet<int> ecmPids = [];

    public bool PatSeen => patVersion >= 0;

    public bool Complete => PatSeen && pmtPids.Keys.All(programs.ContainsKey);

    public IReadOnlyCollection<int> EcmPids => ecmPids;

    public IEnumerable<int> PmtPids => pmtPids.Values.Distinct();

    public IReadOnlyList<int> PmtPidsNotRead =>
        [.. pmtPids.Where(listed => !programs.ContainsKey(listed.Key)).Select(listed => listed.Value).Distinct().Order()];

    public IReadOnlyList<ReadOnlyMemory<byte>> PatSections =>
        [.. patBytes.OrderBy(part => part.Key).Select(part => (ReadOnlyMemory<byte>)part.Value)];

    public bool Lists(int programme) => pmtPids.ContainsKey(programme);

    public int? PmtPidOf(int programme) => pmtPids.TryGetValue(programme, out int pid) ? pid : null;

    public ReadOnlyMemory<byte>? PmtOf(int programme) =>
        pmtBytes.TryGetValue(programme, out byte[]? section) ? new ReadOnlyMemory<byte>(section) : (ReadOnlyMemory<byte>?)null;

    public IReadOnlyList<int> EcmPidsOf(int programme) => ecmsByProgramme.GetValueOrDefault(programme) ?? [];

    public IReadOnlyList<int> StreamPidsOf(int programme) =>
        programs.TryGetValue(programme, out IReadOnlyList<(int Pid, int? Ecm)>? streams) ? [.. streams.Select(stream => stream.Pid)] : [];

    public bool IsPmt(int pid) => pmtPids.ContainsValue(pid);

    public bool IsEcm(int pid) => ecmPids.Contains(pid);

    public int? EcmFor(int pid)
    {
        if (ecmByPid.TryGetValue(pid, out int ecm))
        {
            return ecm;
        }

        return Complete && ecmPids.Count is 1 ? ecmPids.First() : null;
    }

    public void ReadPat(ReadOnlySpan<byte> section)
    {
        if (section[0] is not PatTableId || !PsiSection.IsCurrent(section))
        {
            return;
        }

        int version = PsiSection.Version(section);
        if (version != patVersion)
        {
            patSections.Clear();
            patBytes.Clear();
            patVersion = version;
        }

        Dictionary<int, int> listed = [];
        ReadOnlySpan<byte> loop = PsiSection.Body(section);
        for (int offset = 0; offset + 4 <= loop.Length; offset += 4)
        {
            int programme = BinaryPrimitives.ReadUInt16BigEndian(loop[offset..]);
            int pid = BinaryPrimitives.ReadUInt16BigEndian(loop[(offset + 2)..]) & NoPid;
            if (programme is not 0)
            {
                listed[programme] = pid;
            }
        }

        patSections[section[6]] = listed;
        patBytes[section[6]] = Kept(patBytes.GetValueOrDefault(section[6]), section);
        Dictionary<int, int> merged = [];
        foreach ((int programme, int pid) in patSections.OrderBy(part => part.Key).SelectMany(part => part.Value))
        {
            merged[programme] = pid;
        }

        Dictionary<int, int> before = pmtPids;
        pmtPids = merged;

        foreach (int gone in programs.Keys.Where(programme => Moved(before, programme)).ToList())
        {
            programs.Remove(gone);
            programEcms.Remove(gone);
            pmtBytes.Remove(gone);
        }

        Rebuild();
    }

    public void ReadPmt(ReadOnlySpan<byte> section)
    {
        if (section[0] is not PmtTableId || !PsiSection.IsCurrent(section))
        {
            return;
        }

        int programme = PsiSection.Extension(section);
        if (!pmtPids.ContainsKey(programme))
        {
            return;
        }

        ReadOnlySpan<byte> body = PsiSection.Body(section);
        if (body.Length < 4)
        {
            return;
        }

        int infoLength = BinaryPrimitives.ReadUInt16BigEndian(body[2..]) & 0x0FFF;
        if (4 + infoLength > body.Length)
        {
            return;
        }

        int? programEcm = EcmIn(body.Slice(4, infoLength));
        List<(int Pid, int? Ecm)> streams = [];

        ReadOnlySpan<byte> rest = body[(4 + infoLength)..];
        while (rest.Length >= 5)
        {
            int pid = BinaryPrimitives.ReadUInt16BigEndian(rest[1..]) & NoPid;
            int esInfoLength = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(rest[3..]) & 0x0FFF, rest.Length - 5);

            streams.Add((pid, EcmIn(rest.Slice(5, esInfoLength)) ?? programEcm));
            rest = rest[(5 + esInfoLength)..];
        }

        programs[programme] = streams;
        programEcms[programme] = programEcm;
        pmtBytes[programme] = Kept(pmtBytes.GetValueOrDefault(programme), section);
        Rebuild();
    }

    private bool Moved(Dictionary<int, int> before, int programme) =>
        !pmtPids.TryGetValue(programme, out int pid) || before.GetValueOrDefault(programme, pid) != pid;

    private static byte[] Kept(byte[]? held, ReadOnlySpan<byte> section) =>
        held is not null && section.SequenceEqual(held) ? held : section.ToArray();

    private int? EcmIn(ReadOnlySpan<byte> descriptors)
    {
        ReadOnlySpan<byte> rest = descriptors;

        while (rest.Length >= 2)
        {
            int length = Math.Min(rest[1], rest.Length - 2);
            ReadOnlySpan<byte> content = rest.Slice(2, length);

            if (rest[0] is CaDescriptorTag && length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(content) == caSystemId)
            {
                int pid = BinaryPrimitives.ReadUInt16BigEndian(content[2..]) & NoPid;

                return pid is 0 or NoPid ? null : pid;
            }

            rest = rest[(2 + length)..];
        }

        return null;
    }

    private void Rebuild()
    {
        Dictionary<int, int> byPid = [];
        HashSet<int> ecms = [.. programEcms.Values.OfType<int>()];
        Dictionary<int, IReadOnlyList<int>> byProgramme = [];

        foreach ((int programme, IReadOnlyList<(int Pid, int? Ecm)> streams) in programs)
        {
            int?[] named = [programEcms.GetValueOrDefault(programme), .. streams.Select(stream => stream.Ecm)];
            byProgramme[programme] = [.. named.OfType<int>().Distinct().Order()];
        }

        foreach ((int pid, int? ecm) in programs.Values.SelectMany(streams => streams))
        {
            if (ecm is int named)
            {
                byPid[pid] = named;
                ecms.Add(named);
            }
        }

        ecmByPid = byPid;
        ecmPids = ecms;
        ecmsByProgramme = byProgramme;
    }
}
