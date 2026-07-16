using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlanViewer.Cli.ReplSurface;

internal static class OpenedFilePathResolver
{
    private const int MacOsGetPath = 50;

    public static string GetFinalPath(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OperatingSystem.IsWindows()
            ? GetWindowsPath(stream.SafeFileHandle)
            : OperatingSystem.IsLinux()
                ? GetLinuxPath(stream.SafeFileHandle)
                : OperatingSystem.IsMacOS()
                    ? GetMacOsPath(stream.SafeFileHandle)
                    : throw new IOException("Secure opened-file path validation is not supported on this platform.");
    }

    private static string GetLinuxPath(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt64();
        var target = new FileInfo($"/proc/self/fd/{descriptor}").ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException("Could not resolve the opened file handle.");
        return Path.GetFullPath(target.FullName);
    }

    private static string GetWindowsPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            throw CreateNativeIOException("Could not resolve the opened Windows file handle.");
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw CreateNativeIOException("Could not resolve the opened Windows file handle.");
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];
        return Path.GetFullPath(path);
    }

    private static string GetMacOsPath(SafeFileHandle handle)
    {
        var buffer = new byte[4096];
        if (Fcntl(handle.DangerousGetHandle().ToInt32(), MacOsGetPath, buffer) != 0)
            throw CreateNativeIOException("Could not resolve the opened macOS file handle.");
        var terminator = Array.IndexOf(buffer, (byte)0);
        if (terminator < 0)
            terminator = buffer.Length;
        return Path.GetFullPath(Encoding.UTF8.GetString(buffer, 0, terminator));
    }

    private static IOException CreateNativeIOException(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fileDescriptor, int command, byte[] buffer);
}
