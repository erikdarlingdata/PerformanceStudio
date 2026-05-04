using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlanViewer.Ssms.Installer
{
    class Program
    {
        static readonly (string Label, string VsixInstallerPath)[] SsmsVersions =
        {
            ("SSMS 22", @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe"),
            ("SSMS 21", @"C:\Program Files\Microsoft SQL Server Management Studio 21\Common7\IDE\VSIXInstaller.exe"),
        };

        static int Main(string[] args)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine(" SQL Performance Studio — SSMS Extension");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            var vsixPath = FindVsix(args);
            if (vsixPath == null)
            {
                Console.WriteLine("ERROR: Could not find PlanViewer.Ssms.vsix.");
                Console.WriteLine("Place it in the same folder as this installer, or pass the path as an argument.");
                WaitForKey();
                return 1;
            }

            Console.WriteLine($"VSIX: {vsixPath}");
            Console.WriteLine();

            var installed = SsmsVersions.Where(v => File.Exists(v.VsixInstallerPath)).ToArray();
            if (installed.Length == 0)
            {
                Console.WriteLine("ERROR: No supported SSMS installation found.");
                Console.WriteLine("Supported: SSMS 21, SSMS 22");
                WaitForKey();
                return 1;
            }

            bool anyFailed = false;
            foreach (var (label, installerPath) in installed)
            {
                Console.WriteLine($"Found {label} — installing...");

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = $"/admin \"{vsixPath}\"",
                    UseShellExecute = false,
                };

                try
                {
                    var proc = Process.Start(psi);
                    proc.WaitForExit();

                    if (proc.ExitCode == 0)
                    {
                        Console.WriteLine($"  OK — installed into {label}. Restart SSMS to activate.");
                    }
                    else
                    {
                        Console.WriteLine($"  FAILED (exit code {proc.ExitCode}).");
                        anyFailed = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAILED: {ex.Message}");
                    anyFailed = true;
                }
                Console.WriteLine();
            }

            if (anyFailed)
            {
                Console.WriteLine("One or more installations failed.");
                WaitForKey();
                return 1;
            }

            Console.WriteLine("Done. Restart SSMS to activate the extension.");
            WaitForKey();
            return 0;
        }

        static string FindVsix(string[] args)
        {
            // 1. Explicit argument
            if (args.Length > 0 && File.Exists(args[0]))
                return Path.GetFullPath(args[0]);

            // 2. Same directory as this exe
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(exeDir, "PlanViewer.Ssms.vsix");
            if (File.Exists(candidate))
                return candidate;

            // 3. Look in common build output locations relative to exe
            foreach (var sub in new[] { ".", @"..\bin\Release", @"..\bin\Debug" })
            {
                candidate = Path.GetFullPath(Path.Combine(exeDir, sub, "PlanViewer.Ssms.vsix"));
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        static void WaitForKey()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            try { Console.ReadKey(true); } catch { }
        }
    }
}
