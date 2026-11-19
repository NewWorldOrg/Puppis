using System.Net.Sockets;

namespace Puppis.Driver.CardLending;

public static class HopLimit
{
    public const int Least = 1;

    public const int Most = 8;

    public static void Apply(Socket socket, int hops)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfLessThan(hops, Least);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hops, Most);

        if (socket.AddressFamily is AddressFamily.InterNetworkV6)
        {
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, hops);

            if (socket.DualMode)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, hops);
            }

            return;
        }

        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, hops);
    }

    public static int Of(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        SocketOptionLevel level = socket.AddressFamily is AddressFamily.InterNetworkV6
            ? SocketOptionLevel.IPv6
            : SocketOptionLevel.IP;

        return (int)socket.GetSocketOption(level, SocketOptionName.IpTimeToLive)!;
    }
}
