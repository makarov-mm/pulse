using System.Net;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal enum TcpState
{
    Closed = 1, Listening, SynSent, SynReceived, Established, FinWait1, FinWait2, CloseWait, Closing, LastAck, TimeWait, DeleteTcb
}

internal sealed record TcpConnection(int Pid, bool IPv6, IPAddress LocalAddress, int LocalPort,
    IPAddress RemoteAddress, int RemotePort, TcpState State)
{
    public string Local => Endpoint(LocalAddress, LocalPort);
    // A listening socket has no peer.
    public string Remote => State == TcpState.Listening ? "" : Endpoint(RemoteAddress, RemotePort);

    private string Endpoint(IPAddress address, int port) => IPv6 ? $"[{address}]:{port}" : $"{address}:{port}";
}

internal static class TcpConnections
{
    private const int IPv4Family = 2, IPv6Family = 23, OwnerPidAll = 5;
    private const uint InsufficientBuffer = 122;
    // MIB_TCPROW_OWNER_PID and MIB_TCP6ROW_OWNER_PID. Both tables start with a 4-byte row count.
    private const int IPv4Row = 24, IPv6Row = 56;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref uint bytes, bool sorted, int family, int tableClass, uint reserved);

    // Every TCP socket with the process that owns it. A family that cannot be read is left out.
    public static List<TcpConnection> Read(out bool ipv4Available, out bool ipv6Available)
    {
        var connections = new List<TcpConnection>();
        ipv4Available = ReadFamily(connections, IPv4Family);
        ipv6Available = ReadFamily(connections, IPv6Family);
        return connections;
    }

    private static bool ReadFamily(List<TcpConnection> connections, int family)
    {
        nint table = 0;
        try
        {
            uint bytes = 0;
            uint status = GetExtendedTcpTable(0, ref bytes, false, family, OwnerPidAll, 0);
            // The table can grow between the sizing call and the read.
            for (int attempt = 0; attempt < 3 && status == InsufficientBuffer; attempt++)
            {
                Marshal.FreeHGlobal(table);
                table = 0;
                bytes += 4096;
                table = Marshal.AllocHGlobal((nint)bytes);
                status = GetExtendedTcpTable(table, ref bytes, false, family, OwnerPidAll, 0);
            }
            if (status != 0) return false;
            if (table == 0 || bytes < 4) return true;

            int size = family == IPv6Family ? IPv6Row : IPv4Row;
            long count = Math.Min((uint)Marshal.ReadInt32(table), (bytes - 4) / (uint)size);
            for (int i = 0; i < count; i++)
            {
                nint row = table + 4 + i * size;
                connections.Add(family == IPv6Family
                    ? new TcpConnection(Marshal.ReadInt32(row, 52), true, Address(row, 0, 16), Port(row, 20),
                        Address(row, 24, 16), Port(row, 44), (TcpState)Marshal.ReadInt32(row, 48))
                    : new TcpConnection(Marshal.ReadInt32(row, 20), false, Address(row, 4, 4), Port(row, 8),
                        Address(row, 12, 4), Port(row, 16), (TcpState)Marshal.ReadInt32(row, 0)));
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static IPAddress Address(nint row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        return new IPAddress(bytes);
    }

    // Ports are stored in network byte order in the low 16 bits of a 32-bit field.
    private static int Port(nint row, int offset) =>
        (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
}
