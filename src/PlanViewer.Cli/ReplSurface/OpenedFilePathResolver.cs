using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlanViewer.Cli.ReplSurface;

internal static class OpenedFilePathResolver
{
    /* macOS resolves an open handle back to its path through libproc rather than fcntl(F_GETPATH),
       because fcntl(2) is VARIADIC - int fcntl(int, int, ...) - and a variadic function cannot be
       called correctly through a plain DllImport on Apple arm64. There the variadic arguments are
       passed on the STACK while a fixed-signature P/Invoke puts them in registers, so the callee
       reads a stack slot we never wrote. It does not fail: fcntl returns 0 for success and writes up
       to MAXPATHLEN bytes through whatever pointer that slot happened to hold, which is an arbitrary
       ~1KB write on every call. The buffer we passed comes back empty. See #441.

       proc_pidfdinfo has a fixed signature, so the ordinary marshalling is correct. */
    private const int ProcPidFdVNodePathInfo = 2;

    /* Offset of vip_path within struct vnode_fdinfowithpath, and the struct's total size, both from
       <sys/proc_info.h>:

         vnode_fdinfowithpath { proc_fileinfo pfi; vnode_info_path vip; }
         proc_fileinfo   = 4 + 4 + 8 + 4 + 4                    =   24
         vnode_info      = vinfo_stat(136) + 4 + 4 + fsid_t(8)  =  152   -> vip_path begins at 176
         vnode_info_path = 152 + MAXPATHLEN(1024)               = 1176
         total           = 24 + 1176                            = 1200

       The size is checked against the call's return value rather than trusted, so a layout change in
       a future macOS fails loudly here instead of quietly handing back the wrong bytes - which is the
       failure mode this whole file just came out of. */
    private const int VNodePathInfoSize = 1200;
    private const int VipPathOffset = 176;

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
        var buffer = new byte[VNodePathInfoSize];
        var written = ProcPidFdInfo(
            Environment.ProcessId,
            handle.DangerousGetHandle().ToInt32(),
            ProcPidFdVNodePathInfo,
            buffer,
            buffer.Length);

        if (written <= 0)
            throw CreateNativeIOException("Could not resolve the opened macOS file handle.");
        if (written != VNodePathInfoSize)
            throw new IOException(
                $"Unexpected vnode_fdinfowithpath size {written}; expected {VNodePathInfoSize}.");

        var terminator = Array.IndexOf(buffer, (byte)0, VipPathOffset);
        if (terminator < 0)
            throw new IOException("Opened macOS file handle resolved to an unterminated path.");
        if (terminator == VipPathOffset)
            throw new IOException("Opened macOS file handle resolved to an empty path.");

        return Path.GetFullPath(
            Encoding.UTF8.GetString(buffer, VipPathOffset, terminator - VipPathOffset));
    }

    private static IOException CreateNativeIOException(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("libproc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static extern int ProcPidFdInfo(
        int processId,
        int fileDescriptor,
        int flavor,
        byte[] buffer,
        int bufferSize);
}
