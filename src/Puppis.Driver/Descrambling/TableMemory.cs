using Puppis.Contracts;

namespace Puppis.Driver.Descrambling;

public sealed record TablesKey(TunerKind Kind, int PhysicalChannel, int? ServiceId, TuneParams? Tune = null);

public sealed record CarriedPmt(int Pid, ReadOnlyMemory<byte> Section);

public sealed class StreamTables
{
    public StreamTables(IEnumerable<ReadOnlyMemory<byte>> pat, IEnumerable<CarriedPmt> pmts, IEnumerable<int> ecmPids)
    {
        ArgumentNullException.ThrowIfNull(pat);
        ArgumentNullException.ThrowIfNull(pmts);
        ArgumentNullException.ThrowIfNull(ecmPids);

        Pat = [.. pat.Select(section => (ReadOnlyMemory<byte>)section.ToArray())];
        Pmts = [.. pmts.Select(pmt => pmt with { Section = pmt.Section.ToArray() })];
        EcmPids = [.. ecmPids.Distinct().Order()];
    }

    public IReadOnlyList<ReadOnlyMemory<byte>> Pat { get; }

    public IReadOnlyList<CarriedPmt> Pmts { get; }

    public IReadOnlyList<int> EcmPids { get; }

    public IReadOnlyList<string> DifferencesFrom(StreamTables carried)
    {
        ArgumentNullException.ThrowIfNull(carried);

        List<string> differences = [];

        if (!SameSections(Pat, carried.Pat))
        {
            differences.Add($"the PAT, version {VersionOf(Pat)} remembered and {VersionOf(carried.Pat)} carried");
        }

        IReadOnlyList<int> pids = Pids(Pmts);
        IReadOnlyList<int> carriedPids = Pids(carried.Pmts);
        if (!pids.SequenceEqual(carriedPids))
        {
            differences.Add($"the PMT PIDs, {Named(pids)} remembered and {Named(carriedPids)} carried");
        }

        differences.AddRange(Pmts.Join(carried.Pmts, pmt => pmt.Pid, pmt => pmt.Pid, Changed).OfType<string>());

        if (!EcmPids.SequenceEqual(carried.EcmPids))
        {
            differences.Add($"the ECM PIDs, {Named(EcmPids)} remembered and {Named(carried.EcmPids)} carried");
        }

        return differences;
    }

    private static string? Changed(CarriedPmt remembered, CarriedPmt carried) =>
        remembered.Section.Span.SequenceEqual(carried.Section.Span)
            ? null
            : $"the PMT on PID 0x{remembered.Pid:X4}, version {VersionOf([remembered.Section])} remembered and {VersionOf([carried.Section])} carried";

    private static IReadOnlyList<int> Pids(IReadOnlyList<CarriedPmt> pmts) => [.. pmts.Select(pmt => pmt.Pid)];

    private static bool SameSections(IReadOnlyList<ReadOnlyMemory<byte>> one, IReadOnlyList<ReadOnlyMemory<byte>> other) =>
        one.Count == other.Count && one.Zip(other).All(pair => pair.First.Span.SequenceEqual(pair.Second.Span));

    private static string VersionOf(IReadOnlyList<ReadOnlyMemory<byte>> sections) =>
        string.Join(" and ", sections.Select(section => PsiSection.Version(section.Span)).Distinct());

    private static string Named(IReadOnlyList<int> pids) =>
        pids.Count is 0 ? "none" : string.Join(", ", pids.Select(pid => $"0x{pid:X4}"));
}

public sealed class TableMemory
{
    public const int DefaultCapacity = 64;

    private readonly int capacity;

    private readonly Lock gate = new();

    private readonly Dictionary<TablesKey, (StreamTables Tables, long Used)> remembered = [];

    private long uses;

    public TableMemory(int capacity = DefaultCapacity)
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

    public StreamTables? Recall(TablesKey key)
    {
        lock (gate)
        {
            if (!remembered.TryGetValue(key, out (StreamTables Tables, long Used) entry))
            {
                return null;
            }

            remembered[key] = (entry.Tables, ++uses);

            return entry.Tables;
        }
    }

    public void Remember(TablesKey key, StreamTables tables)
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

    public void Forget(TablesKey key)
    {
        lock (gate)
        {
            remembered.Remove(key);
        }
    }
}
