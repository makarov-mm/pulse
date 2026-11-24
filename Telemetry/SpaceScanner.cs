using System.Security;

namespace Pulse.Telemetry;

// The scanner publishes each folder's children once. Sizes and read errors can change as it walks the tree.
internal sealed class SpaceNode(string name, SpaceNode? parent, bool isFile, long size = 0, long? allocated = 0)
{
    private long _size = size, _allocated = allocated ?? 0;
    private volatile bool _allocationPartial = allocated is null;
    public long? Allocated => _allocationPartial ? null : Interlocked.Read(ref _allocated);
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

    public void Add(long bytes, long allocatedBytes = 0, bool partial = false)
    {
        for (SpaceNode? node = this; node is not null; node = node.Parent)
        {
            Interlocked.Add(ref node._size, bytes);
            Interlocked.Add(ref node._allocated, allocatedBytes);
            if (partial) node._allocationPartial = true;
        }
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
            var largest = new PriorityQueue<(string Name, long? Allocated), long>();
            int count = 0;
            long total = 0, allocatedTotal = 0;
            int unknown = 0;
            try
            {
                var directory = new DirectoryInfo(folder.Path);
                // A chosen root can itself be a link. A child can also disappear or become a link after discovery.
                if ((directory.Attributes & Skip) != 0) { folder.Unread = true; folder.Add(0, 0, true); continue; }
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
                        long? allocation = FileAllocation.Read(file.FullName, file.Attributes);
                        allocatedTotal += allocation ?? 0;
                        if (allocation is null) unknown++;
                        if (largest.Count < LargestFiles) largest.Enqueue((file.Name, allocation), length);
                        else largest.EnqueueDequeue((file.Name, allocation), length);
                    }
                    catch (Exception error) when (IsReadError(error)) { folder.Unread = true; }
                }
            }
            catch (Exception error) when (IsReadError(error)) { folder.Unread = true; }

            var children = new List<SpaceNode>(directories);
            long remaining = total, allocatedRemaining = allocatedTotal;
            int remainingUnknown = unknown;
            foreach (var ((name, allocation), length) in largest.UnorderedItems)
            {
                children.Add(new SpaceNode(name, folder, true, length, allocation));
                allocatedRemaining -= allocation ?? 0;
                if (allocation is null) remainingUnknown--;
                remaining -= length;
            }
            if (count > LargestFiles)
                children.Add(new SpaceNode("", folder, true, remaining, remainingUnknown == 0 ? allocatedRemaining : null) { Grouped = count - LargestFiles });
            folder.Add(total, allocatedTotal, unknown > 0 || folder.Unread);
            folder.Children = children.ToArray();
            cancel.ThrowIfCancellationRequested();
            for (int i = directories.Count - 1; i >= 0; i--) pending.Push(directories[i]);
        }
    }

    private static bool IsReadError(Exception error) => error is UnauthorizedAccessException or IOException or SecurityException;
}
