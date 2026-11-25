using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal enum NeighborState { Unreachable, Incomplete, Probe, Delay, Stale, Reachable, Permanent }
internal sealed record NetworkNeighbor(string Mac, IReadOnlyList<IPAddress> Addresses, NeighborState State, bool Gateway);
internal sealed record NetworkAdapter(string Id, string Name, string Description, NetworkInterfaceType Type, bool Connected, long? LinkSpeed, string Mac, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> Dns, IReadOnlyList<NetworkNeighbor> Neighbors);

internal static class NetworkDevices
{
    private const int IPv4Family = 2, IPv6Family = 23;
    private const int FirstRow = 8, RowBytes = 88, MacBytes = 6;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable2(ushort family, out nint table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint table);

    private readonly record struct Neighbor(IPAddress Address, string Mac, NeighborState State);

    public static List<NetworkAdapter> Read()
    {
        var adapters = new List<NetworkAdapter>();
        NetworkInterface[] interfaces;

        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return adapters;
        }

        Dictionary<int, List<Neighbor>> neighbors = Neighbors();

        foreach (NetworkInterface adapter in interfaces)
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            bool connected = adapter.OperationalStatus == OperationalStatus.Up;
            string mac = Mac(adapter.GetPhysicalAddress().GetAddressBytes());

            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                List<IPAddress> addresses = [.. properties.UnicastAddresses.Select(a => a.Address).Order(AddressOrder)];

                List<IPAddress> gateways =
                [
                    .. properties.GatewayAddresses
                        .Select(g => Plain(g.Address))
                        .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Distinct()
                ];

                IEnumerable<Neighbor> seen = Indexes(properties).SelectMany(index => neighbors.GetValueOrDefault(index) ?? []);
                long speed = adapter.Speed;
                adapters.Add(new NetworkAdapter(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType, connected, connected && speed > 0 ? speed : null, mac, addresses, gateways, properties.DnsAddresses.ToArray(), Devices(seen, gateways)));
            }
            catch (NetworkInformationException)
            {
                adapters.Add(new NetworkAdapter(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType, connected, null, mac, [], [], [], []));
            }
        }

        adapters.Sort((a, b) =>
        {
            if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
            int order = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

        return adapters;
    }

    private static HashSet<int> Indexes(IPInterfaceProperties properties)
    {
        var indexes = new HashSet<int>();

        try
        {
            indexes.Add(properties.GetIPv4Properties().Index);
        }
        catch (NetworkInformationException) { }

        try
        {
            indexes.Add(properties.GetIPv6Properties().Index);
        }
        catch (NetworkInformationException) { }

        return indexes;
    }

    private static List<NetworkNeighbor> Devices(IEnumerable<Neighbor> neighbors, List<IPAddress> gateways)
    {
        List<NetworkNeighbor> devices =
        [
            .. neighbors.GroupBy(n => n.Mac).Select(group =>
            {
                List<IPAddress> addresses = [..group.Select(n => n.Address).Distinct().Order(AddressOrder)];
                return new NetworkNeighbor(group.Key, addresses, group.Max(n => n.State), addresses.Any(gateways.Contains));
            })
        ];

        devices.Sort((a, b) => a.Gateway != b.Gateway ? (a.Gateway ? -1 : 1) : AddressOrder.Compare(a.Addresses[0], b.Addresses[0]));
        return devices;
    }

    private static Dictionary<int, List<Neighbor>> Neighbors()
    {
        var result = new Dictionary<int, List<Neighbor>>();
        if (GetIpNetTable2(0, out nint table) != 0 || table == 0) return result;

        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                nint row = table + FirstRow + (nint)i * RowBytes;
                int family = (ushort)Marshal.ReadInt16(row);
                var state = (NeighborState)Marshal.ReadInt32(row, 76);
                if (family is not (IPv4Family or IPv6Family) || Marshal.ReadInt32(row, 72) != MacBytes || state is NeighborState.Unreachable or NeighborState.Incomplete) continue;
                var mac = new byte[MacBytes];
                Marshal.Copy(row + 40, mac, 0, MacBytes);
                if ((mac[0] & 1) != 0 || mac.All(b => b == 0)) continue;
                var address = new byte[family == IPv6Family ? 16 : 4];
                Marshal.Copy(row + (family == IPv6Family ? 8 : 4), address, 0, address.Length);
                int index = Marshal.ReadInt32(row, 28);
                if (!result.TryGetValue(index, out var list)) result[index] = list = [];
                list.Add(new Neighbor(new IPAddress(address), Mac(mac), state));
            }
        }
        finally { FreeMibTable(table); }
        return result;
    }

    private static string Mac(byte[] bytes) => string.Join('-', bytes.Select(b => b.ToString("X2")));
    private static IPAddress Plain(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;

    private static readonly Comparer<IPAddress> AddressOrder = Comparer<IPAddress>.Create((a, b) =>
    {
        int order = Rank(a).CompareTo(Rank(b));
        return order != 0 ? order : a.GetAddressBytes().AsSpan().SequenceCompareTo(b.GetAddressBytes());
    });

    private static int Rank(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork ? 0 : address.IsIPv6LinkLocal ? 2 : 1;
}

internal static class HostNames
{
    private static readonly ConcurrentDictionary<IPAddress, string> Names = new();
    private static readonly SemaphoreSlim Lookups = new(2);

    public static string Find(IPAddress address)
    {
        if (Names.TryGetValue(address, out string? name)) return name;
        if (Names.TryAdd(address, string.Empty)) _ = ResolveAsync(address);
        return string.Empty;
    }

    private static async Task ResolveAsync(IPAddress address)
    {
        await Lookups.WaitAsync().ConfigureAwait(false);

        try
        {
            string host = (await Dns.GetHostEntryAsync(address).ConfigureAwait(false)).HostName;
            if (!IPAddress.TryParse(host, out _)) Names[address] = host;
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
        }
        finally
        {
            Lookups.Release();
        }
    }
}
