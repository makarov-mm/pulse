using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class NetworkRow : ObservableRow
{
    private string _glyph = "", _name = "", _type = "", _address = "", _mac = "", _state = "", _speed = "", _activity = "", _details = "";
    private Thickness _indent;
    internal NetworkRow() { }
    internal (string Adapter, string? Device) Key { get; private set; }
    public string Glyph => _glyph;
    public Thickness Indent => _indent;
    public string Name => _name;
    public string Type => _type;
    public string Address => _address;
    public string Mac => _mac;
    public string State => _state;
    public string Speed => _speed;
    public string Activity => _activity;
    public string Details => _details;

    internal void Update(NetworkAdapter adapter, AdapterSample? sample)
    {
        Key = (adapter.Id, null);
        var lines = new List<string> { adapter.Description };
        AddLine(lines, UiText.Get("NetworkAddress"), adapter.Addresses);
        AddLine(lines, UiText.Get("NetworkGateway"), adapter.Gateways);
        AddLine(lines, "DNS", adapter.Dns);
        Show(adapter.Type switch
            {
                NetworkInterfaceType.Wireless80211 => "",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "",
                _ => ""
            }, 0, adapter.Name, TypeName(adapter.Type), adapter.Addresses.Count == 0 ? "" : adapter.Addresses[0].ToString(),
            adapter.Mac, UiText.Get(adapter.Connected ? "AdapterConnected" : "AdapterDisconnected"),
            adapter.LinkSpeed is null ? "" : Format.BitRate(adapter.LinkSpeed),
            sample is null ? "" : $"↓ {Format.Bytes(sample.Received, true)}   ·   ↑ {Format.Bytes(sample.Sent, true)}",
            string.Join(Environment.NewLine, lines));
    }

    internal void Update(string adapterId, NetworkNeighbor device, string host)
    {
        Key = (adapterId, device.Mac);
        string address = device.Addresses[0].ToString();
        Show(device.Gateway ? "" : "", 26, DeviceName(device, host), UiText.Get(device.Gateway ? "NetworkGateway" : "NetworkDevice"), address, device.Mac, StateLabel(device.State), "", "", string.Join(Environment.NewLine, device.Addresses));
    }

    private void Show(string glyph, double indent, string name, string type, string address, string mac, string state, string speed, string activity, string details)
    {
        Set(ref _glyph, glyph, nameof(Glyph));
        Set(ref _indent, new Thickness(indent, 0, 0, 0), nameof(Indent));
        Set(ref _name, name, nameof(Name));
        Set(ref _type, type, nameof(Type));
        Set(ref _address, address, nameof(Address));
        Set(ref _mac, mac, nameof(Mac));
        Set(ref _state, state, nameof(State));
        Set(ref _speed, speed, nameof(Speed));
        Set(ref _activity, activity, nameof(Activity));
        Set(ref _details, details, nameof(Details));
    }

    private static void AddLine<T>(List<string> lines, string label, IReadOnlyList<T> values)
    {
        if (values.Count != 0)
        {
            lines.Add($"{label}: {string.Join(", ", values)}");
        }
    }

    internal static string DeviceName(NetworkNeighbor device, string host) => host.Length != 0 ? host : device.Addresses[0].ToString();

    internal static string TypeName(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Tunnel => UiText.Get("AdapterTunnel"),
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => UiText.Get("AdapterMobile"),
        _ => UiText.Get("AdapterOther")
    };

    internal static string StateLabel(NeighborState state) => UiText.Get(state switch
        {
            NeighborState.Reachable => "NeighborReachable",
            NeighborState.Permanent => "NeighborStatic",
            _ => "NeighborIdle"
        });
}
