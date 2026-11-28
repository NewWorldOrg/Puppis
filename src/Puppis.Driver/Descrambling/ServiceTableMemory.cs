using Puppis.Contracts;

namespace Puppis.Driver.Descrambling;

public sealed record ServiceTablesKey(TunerKind Kind, int PhysicalChannel, int ServiceId);

public sealed class ServiceTables
{
    public ServiceTables(
        IEnumerable<ReadOnlyMemory<byte>> pat,
        int pmtPid,
        ReadOnlyMemory<byte> pmt,
        IEnumerable<int> ecmPids
    )
    {
        ArgumentNullException.ThrowIfNull(pat);
        ArgumentNullException.ThrowIfNull(ecmPids);

        Pat = [.. pat.Select(section => (ReadOnlyMemory<byte>)section.ToArray())];
        PmtPid = pmtPid;
        Pmt = pmt.ToArray();
        EcmPids = [.. ecmPids.Distinct().Order()];
    }

    public IReadOnlyList<ReadOnlyMemory<byte>> Pat { get; }

    public int PmtPid { get; }

    public ReadOnlyMemory<byte> Pmt { get; }

    public IReadOnlyList<int> EcmPids { get; }

    public IReadOnlyList<string> DifferencesFrom(ServiceTables carried)
    {
        ArgumentNullException.ThrowIfNull(carried);

        List<string> differences = [];

        if (!SameSections(Pat, carried.Pat))
        {
            differences.Add($"the PAT, version {VersionOf(Pat)} remembered and {VersionOf(carried.Pat)} carried");
        }

        if (PmtPid != carried.PmtPid)
        {
            differences.Add($"the PMT PID, 0x{PmtPid:X4} remembered and 0x{carried.PmtPid:X4} carried");
        }

        if (!Pmt.Span.SequenceEqual(carried.Pmt.Span))
        {
            differences.Add($"the PMT, version {VersionOf([Pmt])} remembered and {VersionOf([carried.Pmt])} carried");
        }

        if (!EcmPids.SequenceEqual(carried.EcmPids))
        {
            differences.Add($"the ECM PIDs, {Named(EcmPids)} remembered and {Named(carried.EcmPids)} carried");
        }

        return differences;
    }

    private static bool SameSections(IReadOnlyList<ReadOnlyMemory<byte>> one, IReadOnlyList<ReadOnlyMemory<byte>> other) =>
        one.Count == other.Count && one.Zip(other).All(pair => pair.First.Span.SequenceEqual(pair.Second.Span));

    private static string VersionOf(IReadOnlyList<ReadOnlyMemory<byte>> sections) =>
        string.Join(" and ", sections.Select(section => PsiSection.Version(section.Span)).Distinct());

    private static string Named(IReadOnlyList<int> pids) =>
        pids.Count is 0 ? "none" : string.Join(", ", pids.Select(pid => $"0x{pid:X4}"));
}

public sealed class ServiceTableMemory
{
    public const int DefaultCapacity = 64;

    private readonly int capacity;

    private readonly Lock gate = new();

    private readonly Dictionary<ServiceTablesKey, (ServiceTables Tables, long Used)> remembered = [];

    private long uses;

    public ServiceTableMemory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        this.capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return remembered.Count;
            }
        }
    }

    public ServiceTables? Recall(ServiceTablesKey key)
    {
        lock (gate)
        {
            if (!remembered.TryGetValue(key, out (ServiceTables Tables, long Used) entry))
            {
                return null;
            }

            remembered[key] = (entry.Tables, ++uses);

            return entry.Tables;
        }
    }

    public void Remember(ServiceTablesKey key, ServiceTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        lock (gate)
        {
            remembered[key] = (tables, ++uses);

            while (remembered.Count > capacity)
            {
                remembered.Remove(remembered.MinBy(entry => entry.Value.Used).Key);
            }
        }
    }

    public void Forget(ServiceTablesKey key)
    {
        lock (gate)
        {
            remembered.Remove(key);
        }
    }
}
