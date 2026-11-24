using System.Net;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal enum TcpState
{
    Closed = 1, Listening, SynSent, SynReceived, Established, FinWait1, FinWait2, CloseWait, Closing, LastAck, TimeWait, DeleteTcb
}

[Flags]
internal enum SocketTables
{
    None = 0, Tcp4 = 1, Tcp6 = 2, Udp4 = 4, Udp6 = 8,
    Tcp = Tcp4 | Tcp6, All = Tcp | Udp4 | Udp6
}

// A TCP socket, or a bound UDP port. UDP keeps no connection, so it has neither a peer nor a state.
internal sealed record TcpConnection(int Pid, bool IPv6, IPAddress LocalAddress, int LocalPort,
    IPAddress RemoteAddress, int RemotePort, TcpState State)
{
    public bool Udp { get; init; }
    public string Protocol => (Udp ? "UDP" : "TCP") + (IPv6 ? "v6" : "");
    public string Local => Endpoint(LocalAddress, LocalPort);
    // A listening socket has no peer.
    public string Remote => Udp || State == TcpState.Listening ? "" : Endpoint(RemoteAddress, RemotePort);

    private string Endpoint(IPAddress address, int port) => IPv6 ? $"[{address}]:{port}" : $"{address}:{port}";
}

internal static class TcpConnections
{
    private const int IPv4Family = 2, IPv6Family = 23, OwnerPidAll = 5, UdpOwnerPid = 1;
    private const uint InsufficientBuffer = 122;
    // MIB_TCPROW_OWNER_PID and MIB_TCP6ROW_OWNER_PID. Both tables start with a 4-byte row count.
    private const int IPv4Row = 24, IPv6Row = 56;
    // MIB_UDPROW_OWNER_PID and MIB_UDP6ROW_OWNER_PID.
    private const int IPv4UdpRow = 12, IPv6UdpRow = 28;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref uint bytes, bool sorted, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(nint table, ref uint bytes, bool sorted, int family, int tableClass, uint reserved);

    // Every TCP socket and bound UDP port with the process that owns it. A table that cannot be read is left out.
    public static List<TcpConnection> Read(out SocketTables available)
    {
        var connections = new List<TcpConnection>();
        available = SocketTables.None;
        if (ReadFamily(connections, IPv4Family, false)) available |= SocketTables.Tcp4;
        if (ReadFamily(connections, IPv6Family, false)) available |= SocketTables.Tcp6;
        if (ReadFamily(connections, IPv4Family, true)) available |= SocketTables.Udp4;
        if (ReadFamily(connections, IPv6Family, true)) available |= SocketTables.Udp6;
        return connections;
    }

    private static bool ReadFamily(List<TcpConnection> connections, int family, bool udp)
    {
        uint Query(nint buffer, ref uint size) => udp
            ? GetExtendedUdpTable(buffer, ref size, false, family, UdpOwnerPid, 0)
            : GetExtendedTcpTable(buffer, ref size, false, family, OwnerPidAll, 0);

        nint table = 0;
        try
        {
            uint bytes = 0;
            uint status = Query(0, ref bytes);
            // The table can grow between the sizing call and the read.
            for (int attempt = 0; attempt < 3 && status == InsufficientBuffer; attempt++)
            {
                Marshal.FreeHGlobal(table);
                table = 0;
                bytes += 4096;
                table = Marshal.AllocHGlobal((nint)bytes);
                status = Query(table, ref bytes);
            }
            if (status != 0) return false;
            if (table == 0 || bytes < 4) return true;

            bool ipv6 = family == IPv6Family;
            int size = udp ? (ipv6 ? IPv6UdpRow : IPv4UdpRow) : ipv6 ? IPv6Row : IPv4Row;
            long count = Math.Min((uint)Marshal.ReadInt32(table), (bytes - 4) / (uint)size);
            for (int i = 0; i < count; i++)
            {
                nint row = table + 4 + i * size;
                connections.Add(ReadRow(row, ipv6, udp));
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    internal static TcpConnection ReadRow(nint row, bool ipv6, bool udp)
    {
        if (udp)
        {
            return ipv6
                ? new(Marshal.ReadInt32(row, 24), true, Address(row, 0, 16), Port(row, 20), IPAddress.IPv6None, 0, default) { Udp = true }
                : new(Marshal.ReadInt32(row, 8), false, Address(row, 0, 4), Port(row, 4), IPAddress.None, 0, default) { Udp = true };
        }
        return ipv6
            ? new(Marshal.ReadInt32(row, 52), true, Address(row, 0, 16), Port(row, 20),
                Address(row, 24, 16), Port(row, 44), (TcpState)Marshal.ReadInt32(row, 48))
            : new(Marshal.ReadInt32(row, 20), false, Address(row, 4, 4), Port(row, 8),
                Address(row, 12, 4), Port(row, 16), (TcpState)Marshal.ReadInt32(row, 0));
    }

    private static IPAddress Address(nint row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        // Both TCP6 and UDP6 store a network-order scope ID immediately after each IPv6 address.
        if (length == 16)
        {
            uint scope = unchecked((uint)IPAddress.NetworkToHostOrder(Marshal.ReadInt32(row, offset + length)));
            return new IPAddress(bytes, scope);
        }
        return new IPAddress(bytes);
    }

    // Ports are stored in network byte order in the low 16 bits of a 32-bit field.
    private static int Port(nint row, int offset) =>
        (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
}
