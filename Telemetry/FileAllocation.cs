using Microsoft.Win32.SafeHandles;
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
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll")]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out StandardInfo info, uint size);

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CompressionInfo { public long CompressedSize; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetCompressionInfo(SafeFileHandle handle, int informationClass, out CompressionInfo info, uint size);

    public static long? Read(string path, FileAttributes attributes = 0)
    {
        using var handle = CreateFileW(path, 0x80, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        if (handle.IsInvalid) return null;
        if ((attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0 &&
            GetCompressionInfo(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>()))
            return compression.CompressedSize;
        return GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>())
            ? info.AllocationSize : null;
    }
}
