using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal static class FileAllocation
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    [DllImport("kernel32.dll")]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out StandardInfo info, uint size);

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CompressionInfo { public long CompressedSize; }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetCompressionInfo(SafeFileHandle handle, int informationClass, out CompressionInfo info, uint size);

    internal readonly record struct Entry(string Name, FileAttributes Attributes, long Length, long? Allocation);
    internal const int DirectoryBufferSize = 64 * 1024;
    private const FileAttributes SpecialAllocation = FileAttributes.SparseFile | FileAttributes.Compressed;
    private const FileAttributes Skip = FileAttributes.ReparsePoint | FileAttributes.Offline;

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadDirectory(SafeFileHandle handle, int infoClass, [Out] byte[] buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);

    // FILE_FULL_DIR_INFO supplies logical and allocated lengths together for a directory page.
    internal static IEnumerable<Entry> Enumerate(DirectoryInfo directory, byte[] buffer)
    {
        using var handle = CreateFileW(Extended(directory.FullName), 0x81, FileShare.ReadWrite | FileShare.Delete, 0,
            FileMode.Open, 0x02000000 | 0x00200000, 0); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        if (!handle.IsInvalid)
        {
            if (ReadAttributes(handle, 9, out var tag, (uint)Marshal.SizeOf<AttributeTag>()) && (tag.Attributes & Skip) != 0)
                throw new IOException(directory.FullName);
            bool first = true;
            while (true)
            {
                if (!ReadDirectory(handle, 14, buffer, (uint)buffer.Length)) // FileFullDirectoryInfo
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES
                    if (first && error is 1 or 50 or 87 or 124) break; // File information class unsupported.
                    throw new IOException(directory.FullName, new Win32Exception(error));
                }

                first = false;
                for (int offset = 0; ;)
                {
                    const int nameOffset = 68;
                    if (offset > buffer.Length - nameOffset) throw new InvalidDataException();
                    int next = BitConverter.ToInt32(buffer, offset);
                    int available = next == 0 ? buffer.Length - offset : next;
                    int nameBytes = BitConverter.ToInt32(buffer, offset + 60);
                    if (available < nameOffset || available > buffer.Length - offset || nameBytes <= 0 ||
                        (nameBytes & 1) != 0 || nameBytes > available - nameOffset) throw new InvalidDataException();
                    var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
                    if ((attributes & Skip) == 0)
                    {
                        string name = new(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(offset + nameOffset, nameBytes)));
                        if (name is not ("." or ".."))
                        {
                            long length = BitConverter.ToInt64(buffer, offset + 40);
                            long? allocation = BitConverter.ToInt64(buffer, offset + 48);
                            if ((attributes & SpecialAllocation) != 0 && (attributes & FileAttributes.Directory) == 0)
                                allocation = Read(Path.Combine(directory.FullName, name), attributes);
                            yield return new Entry(name, attributes, length, allocation);
                        }
                    }
                    if (next == 0) break;
                    offset += next;
                }
            }
        }

        var options = new EnumerationOptions { AttributesToSkip = Skip, IgnoreInaccessible = false };
        foreach (var info in directory.EnumerateFileSystemInfos("*", options))
        {
            Entry entry;
            try
            {
                entry = info is FileInfo file
                    ? new Entry(file.Name, file.Attributes, file.Length, Read(file.FullName, file.Attributes))
                    : new Entry(info.Name, info.Attributes, 0, 0);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                entry = new Entry(info.Name, 0, -1, null);
            }
            yield return entry;
        }
    }

    public static long? Read(string path, FileAttributes attributes = 0)
    {
        using var handle = CreateFileW(Extended(path), 0x80, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        if (handle.IsInvalid) return null;

        if ((attributes & SpecialAllocation) != 0 && GetCompressionInfo(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>()))
        {
            return compression.CompressedSize;
        }

        return GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()) ? info.AllocationSize : null;
    }

    // The extended-length form opens a full path beyond MAX_PATH whatever the system's long path setting is.
    private static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
