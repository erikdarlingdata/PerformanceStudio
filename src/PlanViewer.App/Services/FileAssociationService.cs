using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PlanViewer.App.Services;

/// <summary>
/// Registers the <c>.sqlplan</c> file association so a double-clicked plan opens in
/// Performance Studio. Best-effort and idempotent — it must never throw into startup.
///
/// The open itself is handled by the existing argv + named-pipe path in
/// <c>Program.Main</c>/<c>MainWindow</c>: once the OS launches the app with the plan
/// path as an argument, it loads like any other file. This service only makes the OS
/// route the double-click to us.
///
/// - <b>Windows</b>: HKCU\Software\Classes ProgId + open command, re-registered each
///   launch so the path tracks Velopack's versioned install directory.
/// - <b>Linux</b>: a freedesktop <c>.desktop</c> entry + MIME glob, with the desktop
///   databases refreshed only when something was actually written.
/// - <b>macOS</b>: no-op. Launch Services reads <c>CFBundleDocumentTypes</c> from the
///   bundle's Info.plist. (Loading the opened plan additionally needs Avalonia's
///   <c>FileActivatedEventArgs</c> to deliver the path; Avalonia 11.3.17 does not
///   expose it, so macOS double-click currently launches the app without the plan.)
/// </summary>
public static class FileAssociationService
{
    private const string Extension = ".sqlplan";
    private const string ProgId = "SQLPerformanceStudio.sqlplan";
    private const string FriendlyType = "SQL Server Execution Plan";
    private const string LinuxMimeType = "application/x-sqlplan";
    private const string LinuxDesktopFile = "sqlperformancestudio.desktop";

    /// <summary>Registers the association for the currently running executable.</summary>
    public static void RegisterForCurrentExecutable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            if (OperatingSystem.IsWindows())
                RegisterWindows(exePath);
            else if (OperatingSystem.IsLinux())
                RegisterLinux(exePath);
            // macOS: handled declaratively by Info.plist; nothing to do at runtime.
        }
        catch
        {
            // The association is a convenience; a failure here must never block launch.
        }
    }

    /// <summary>Removes the association. Called from the Velopack uninstall hook.</summary>
    public static void Unregister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                UnregisterWindows();
            else if (OperatingSystem.IsLinux())
                UnregisterLinux();
        }
        catch
        {
            // Uninstall cleanup is best-effort.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exePath)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        if (classes == null)
            return;

        var desiredCommand = $"\"{exePath}\" \"%1\"";

        // Skip the rewrite (and the shell-change broadcast) when already current — this
        // runs on every launch, so the common case must be cheap and side-effect free.
        using (var existing = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
        {
            if (existing?.GetValue(null) as string == desiredCommand)
                return;
        }

        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue(null, FriendlyType);
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exePath}\",0");
            using (var cmd = progId.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, desiredCommand);
        }

        using (var ext = classes.CreateSubKey(Extension))
        {
            // Become the default only when nothing else owns it. We never overwrite an
            // existing default (e.g. SSMS) — we just add ourselves to "Open with" so the
            // user can choose us. (Windows' UserChoice hash blocks silently forcing it.)
            if (ext.GetValue(null) is not string current || string.IsNullOrEmpty(current))
                ext.SetValue(null, ProgId);
            using var progIds = ext.CreateSubKey("OpenWithProgids");
            progIds.SetValue(ProgId, string.Empty);
        }

        NotifyShellAssociationsChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes == null)
            return;

        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);

        using (var ext = classes.OpenSubKey(Extension, writable: true))
        {
            if (ext != null)
            {
                if (ext.GetValue(null) as string == ProgId)
                    ext.DeleteValue(string.Empty, throwOnMissingValue: false);
                using var progIds = ext.OpenSubKey("OpenWithProgids", writable: true);
                progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }

        NotifyShellAssociationsChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyShellAssociationsChanged()
    {
        try
        {
            // SHCNE_ASSOCCHANGED, SHCNF_IDLIST — tell Explorer the associations changed.
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Cosmetic refresh only; failure just means Explorer updates a bit later.
        }
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [SupportedOSPlatform("linux")]
    private static void RegisterLinux(string exePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return;

        var appsDir = Path.Combine(home, ".local", "share", "applications");
        var mimeRoot = Path.Combine(home, ".local", "share", "mime");
        var mimeDir = Path.Combine(mimeRoot, "packages");
        Directory.CreateDirectory(appsDir);
        Directory.CreateDirectory(mimeDir);

        var desktop = string.Join("\n",
            "[Desktop Entry]",
            "Type=Application",
            "Name=Performance Studio",
            "GenericName=SQL Server Execution Plan Viewer",
            $"Exec=\"{exePath}\" %f",
            "Terminal=false",
            "Categories=Development;Database;",
            $"MimeType={LinuxMimeType};",
            "");

        var mime = string.Join("\n",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">",
            $"  <mime-type type=\"{LinuxMimeType}\">",
            "    <comment>SQL Server Execution Plan</comment>",
            "    <glob pattern=\"*.sqlplan\"/>",
            "  </mime-type>",
            "</mime-info>",
            "");

        var changed = WriteIfChanged(Path.Combine(appsDir, LinuxDesktopFile), desktop);
        changed |= WriteIfChanged(Path.Combine(mimeDir, "sqlperformancestudio.xml"), mime);
        if (!changed)
            return;

        // Best-effort refresh; if these tools are absent the files are still written and
        // most desktop environments pick them up after a relog.
        RunQuiet("update-mime-database", mimeRoot);
        RunQuiet("update-desktop-database", appsDir);
    }

    [SupportedOSPlatform("linux")]
    private static void UnregisterLinux()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return;

        var appsDir = Path.Combine(home, ".local", "share", "applications");
        var mimeRoot = Path.Combine(home, ".local", "share", "mime");
        DeleteIfExists(Path.Combine(appsDir, LinuxDesktopFile));
        DeleteIfExists(Path.Combine(mimeRoot, "packages", "sqlperformancestudio.xml"));
        RunQuiet("update-mime-database", mimeRoot);
        RunQuiet("update-desktop-database", appsDir);
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        File.WriteAllText(path, content);
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void RunQuiet(string fileName, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, $"\"{argument}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(psi);
        }
        catch
        {
            // update-mime-database / update-desktop-database not installed — ignore.
        }
    }
}
