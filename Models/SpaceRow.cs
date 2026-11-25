using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class SpaceRow : ObservableRow
{
    internal SpaceNode Node { get; }
    private string _name = "", _amount = "", _share = "";
    private long _bytes = -1, _total = -1;
    private long? _allocatedBytes;
    private bool _unread;
    internal SpaceRow(SpaceNode node) => Node = node;
    public string Name => _name;
    public string Glyph => Node.IsFile ? "\uE7C3" : "\uE8B7";
    public string Amount => _amount;
    private string _allocated = "";
    public string Allocated => _allocated;
    public string Share => _share;

    public override string ToString() => Name;

    internal static string Label(SpaceNode node) => node.Grouped > 0
        ? UiText.Format("SpaceMoreFiles", node.Grouped)
        : node.Unread
            ? $"{node.Name} ({UiText.Get("SpaceUnread")})"
            : node.Name;

    internal void Refresh(long size, long total)
    {
        if (_bytes < 0 || _unread != Node.Unread)
        {
            _unread = Node.Unread;
            Set(ref _name, Label(Node), nameof(Name));
        }

        var allocated = Node.Allocated;
        if (_bytes < 0 || _allocatedBytes != allocated)
        {
            _allocatedBytes = allocated;
            Set(ref _allocated, Format.Bytes(allocated), nameof(Allocated));
        }

        if (_bytes != size) Set(ref _amount, Format.Bytes(size), nameof(Amount));
        if (_bytes != size || _total != total) Set(ref _share, total > 0 ? Format.Percent(100.0 * size / total) : "—", nameof(Share));

        _bytes = size;
        _total = total;
    }
}
