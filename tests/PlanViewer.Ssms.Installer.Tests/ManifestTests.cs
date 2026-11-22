using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The installer must not ask Windows for administrator rights. Only Microsoft's VSIXInstaller is started
/// elevated, and Windows names that program in its permission prompt.
/// </summary>
public class ManifestTests
{
    [Fact]
    public void TheInstallerRunsAsTheUserWhoStartedIt()
    {
        var manifest = ReadEmbeddedManifest(typeof(InstallerProgram).Assembly.Location);

        Assert.Contains("requestedExecutionLevel level=\"asInvoker\"", manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
        Assert.DoesNotContain("highestAvailable", manifest);
    }

    // The Win32 manifest is a native resource of the exe (type RT_MANIFEST, id 1), not a .NET resource.
    static string ReadEmbeddedManifest(string exePath)
    {
        const uint LoadLibraryAsDataFile = 0x2;
        var module = LoadLibraryEx(exePath, IntPtr.Zero, LoadLibraryAsDataFile);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var resource = FindResource(module, (IntPtr)1, (IntPtr)24);
            if (resource == IntPtr.Zero)
                throw new InvalidOperationException("The installer has no embedded manifest.");
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32")]
    static extern IntPtr LockResource(IntPtr resource);

    [DllImport("kernel32", SetLastError = true)]
    static extern uint SizeofResource(IntPtr module, IntPtr resource);
}
