using System.Security;

namespace Pulse.Telemetry;

// The scanner publishes each folder's children once. Sizes and read errors can change as it walks the tree.
internal sealed class SpaceNode(string name, SpaceNode? parent, bool isFile, long size = 0)
{
    private long _size = size;
    private volatile SpaceNode[] _children = [];
    private volatile bool _unread;

    public string Name => name;
    public SpaceNode? Parent => parent;
    public bool IsFile => isFile;
    public int Grouped { get; init; }
    public long Size => Interlocked.Read(ref _size);
    public SpaceNode[] Children { get => _children; set => _children = value; }
    public bool Unread { get => _unread; set => _unread = value; }
    public string Path
    {
        get
        {
            var parts = new Stack<string>();
            for (SpaceNode? node = this; node is not null; node = node.Parent) parts.Push(node.Name);
            return System.IO.Path.Combine(parts.ToArray());
        }
    }

    public void Add(long bytes)
    {
        for (SpaceNode? node = this; node is not null; node = node.Parent) Interlocked.Add(ref node._size, bytes);
    }
}

internal static class SpaceScanner
{
    private const int LargestFiles = 20;
    private const FileAttributes Skip = FileAttributes.ReparsePoint | FileAttributes.Offline;

    public static void Scan(SpaceNode root, CancellationToken cancel)
    {
        var options = new EnumerationOptions { AttributesToSkip = Skip, IgnoreInaccessible = false };
        var pending = new Stack<SpaceNode>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            cancel.ThrowIfCancellationRequested();
            var directories = new List<SpaceNode>();
            var largest = new PriorityQueue<string, long>();
            int count = 0;
            long total = 0;
            try
            {
                var directory = new DirectoryInfo(folder.Path);
                // A chosen root can itself be a link. A child can also disappear or become a link after discovery.
                if ((directory.Attributes & Skip) != 0) { folder.Unread = true; continue; }
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    if (cancel.IsCancellationRequested) break;
                    if (entry is not FileInfo file)
                    {
                        directories.Add(new SpaceNode(entry.Name, folder, false));
                        continue;
                    }
                    try
                    {
                        long length = file.Length;
                        total += length;
                        count++;
                        if (largest.Count < LargestFiles) largest.Enqueue(file.Name, length);
                        else largest.EnqueueDequeue(file.Name, length);
                    }
                    catch (Exception error) when (IsReadError(error)) { folder.Unread = true; }
                }
            }
            catch (Exception error) when (IsReadError(error)) { folder.Unread = true; }

            var children = new List<SpaceNode>(directories);
            long remaining = total;
            foreach (var (name, length) in largest.UnorderedItems)
            {
                children.Add(new SpaceNode(name, folder, true, length));
                remaining -= length;
            }
            if (count > LargestFiles)
                children.Add(new SpaceNode("", folder, true, remaining) { Grouped = count - LargestFiles });
            folder.Add(total);
            folder.Children = children.ToArray();
            cancel.ThrowIfCancellationRequested();
            for (int i = directories.Count - 1; i >= 0; i--) pending.Push(directories[i]);
        }
    }

    private static bool IsReadError(Exception error) => error is UnauthorizedAccessException or IOException or SecurityException;
}
