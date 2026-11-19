using System.Net;
using System.Net.Sockets;

namespace Puppis.Driver.CardLending;

public sealed class LendingDoor(int unborrowed = LendingDoor.MostUnborrowed, int perAddress = LendingDoor.PerAddress)
{
    public const int MostUnborrowed = 8;

    public const int PerAddress = 3;

    private const int SubnetBytes = 8;

    private readonly Lock gate = new();

    private readonly Dictionary<string, Seat> seats = [];

    private long arrivals;

    public bool TryAdmit(string connection, IPAddress address, DateTimeOffset now, out string? evicted)
    {
        ArgumentNullException.ThrowIfNull(address);

        evicted = null;
        string area = AreaOf(address);

        lock (gate)
        {
            List<KeyValuePair<string, Seat>> waiting = [.. seats.Where(seat => !seat.Value.Borrowed)];
            List<KeyValuePair<string, Seat>> sameArea = [.. waiting.Where(seat => seat.Value.Area == area)];

            if (sameArea.Count >= perAddress && !TryTurnAway(sameArea, out evicted))
            {
                return false;
            }

            if (evicted is null && waiting.Count >= unborrowed && !TryTurnAway(waiting, out evicted))
            {
                return false;
            }

            seats[connection] = new Seat(area, ++arrivals, now);

            return true;
        }
    }

    public void Proven(string connection)
    {
        lock (gate)
        {
            if (seats.TryGetValue(connection, out Seat? seat))
            {
                seat.Proven = true;
            }
        }
    }

    public void Borrowed(string connection)
    {
        lock (gate)
        {
            if (seats.TryGetValue(connection, out Seat? seat))
            {
                seat.Borrowed = true;
            }
        }
    }

    public bool HasBorrowed(string connection)
    {
        lock (gate)
        {
            return seats.TryGetValue(connection, out Seat? seat) && seat.Borrowed;
        }
    }

    public IReadOnlyList<string> Overdue(DateTimeOffset now, TimeSpan life)
    {
        lock (gate)
        {
            return [.. seats.Where(seat => !seat.Value.Borrowed && now - seat.Value.Since > life).Select(seat => seat.Key)];
        }
    }

    public void Left(string connection)
    {
        lock (gate)
        {
            seats.Remove(connection);
        }
    }

    private bool TryTurnAway(List<KeyValuePair<string, Seat>> candidates, out string? evicted)
    {
        evicted = null;
        KeyValuePair<string, Seat>[] unproven = [.. candidates.Where(seat => !seat.Value.Proven)];
        if (unproven.Length is 0)
        {
            return false;
        }

        evicted = unproven.MinBy(seat => seat.Value.Arrival).Key;
        seats.Remove(evicted);

        return true;
    }

    private static string AreaOf(IPAddress address)
    {
        IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return plain.AddressFamily is AddressFamily.InterNetworkV6
            ? $"{Convert.ToHexString(plain.GetAddressBytes(), 0, SubnetBytes)}/64"
            : plain.ToString();
    }

    private sealed class Seat(string area, long arrival, DateTimeOffset since)
    {
        public string Area { get; } = area;

        public long Arrival { get; } = arrival;

        public DateTimeOffset Since { get; } = since;

        public bool Proven { get; set; }

        public bool Borrowed { get; set; }
    }
}
