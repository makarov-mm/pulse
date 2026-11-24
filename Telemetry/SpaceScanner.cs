using System.Security;

namespace Pulse.Telemetry;

// A folder or file in a disk space scan. The scanner publishes children and sizes while it runs,
// so another thread can read a consistent, possibly incomplete, picture at any time.
internal sealed class SpaceNode(string name, SpaceNode? parent, bool isFile, long size = 0)
{
    private long _size = size;
    private volatile SpaceNode[] _children = [];
    private volatile bool _unread;

    public string Name => name;
    public SpaceNode? Parent => parent;
    public bool IsFile => isFile;
    // Greater than zero for a node that stands for this many smaller files of its folder.
    public int Grouped { get; init; }
    public long Size => Interlocked.Read(ref _size);
    public SpaceNode[] Children { get => _children; set => _children = value; }
    // The folder could not be listed, so its size is incomplete.
    public bool Unread { get => _unread; set => _unread = value; }
    public string Path => parent is null ? name : System.IO.Path.Combine(parent.Path, name);

    // Adds bytes to this folder and every folder above it.
    public void Add(long bytes)
    {
        for (SpaceNode? node = this; node is not null; node = node.Parent) Interlocked.Add(ref node._size, bytes);
    }
}

internal static class SpaceScanner
{
    // Each folder keeps its largest files as separate nodes and groups the rest.
    private const int LargestFiles = 20;

    // Walks the folder tree below node. Sizes are logical file lengths. Reparse points are skipped,
    // which leaves out junctions, symbolic links and cloud files that are not stored locally.
    public static void Scan(SpaceNode node, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var folders = new List<SpaceNode>();
        var files = new List<(string Name, long Size)>();
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var entry in new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", options))
            {
                if (entry is FileInfo file) files.Add((file.Name, file.Length));
                else folders.Add(new SpaceNode(entry.Name, node, false));
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException)
        {
            node.Unread = true;
        }

        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        var children = new List<SpaceNode>(folders);
        long total = 0, grouped = 0;
        for (int i = 0; i < files.Count; i++)
        {
            total += files[i].Size;
            if (i < LargestFiles) children.Add(new SpaceNode(files[i].Name, node, true, files[i].Size));
            else grouped += files[i].Size;
        }
        if (files.Count > LargestFiles)
            children.Add(new SpaceNode("", node, true, grouped) { Grouped = files.Count - LargestFiles });
        node.Children = children.ToArray();
        node.Add(total);

        foreach (var folder in folders) Scan(folder, cancel);
    }
}
