using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;

namespace PlanViewer.Ssms
{
    /// <summary>
    /// Finds and launches SQL Performance Studio with a plan file.
    /// If the app is already running, sends the file via named pipe
    /// so it opens as a new tab instead of a new window.
    /// </summary>
    internal static class AppLauncher
    {
        private const string ExeName = "PlanViewer.App.exe";
        private const string PipeName = "SQLPerformanceStudio_OpenFile";
        private const string RegistryKey = @"SOFTWARE\DarlingData\SQLPerformanceStudio";
        private const string RegistryValue = "InstallPath";

        /// <summary>
        /// Saves plan XML to a temp .sqlplan file and returns the path.
        /// Uses a cryptographically-random suffix so the filename can't be predicted
        /// or preempted by another local process (e.g. planting a symlink at the
        /// expected path before the write lands). FileMode.CreateNew refuses to
        /// overwrite a pre-existing file, closing the race further.
        /// </summary>
        public static string SavePlanToTemp(string planXml)
        {
            var suffix = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            var fileName = "ssms_plan_" + suffix + ".sqlplan";
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                writer.Write(planXml);
            }
            return tempPath;
        }

        /// <summary>
        /// Opens the file in SQL Performance Studio. If the app is already running,
        /// sends the file path via named pipe (opens as a new tab). Otherwise
        /// launches a new instance.
        /// Returns true if the file was sent or launched successfully.
        /// </summary>
        public static bool LaunchApp(string filePath)
        {
            // Try sending to an already-running instance first
            if (TrySendToRunningInstance(filePath))
                return true;

            // No running instance — launch a new one
            string appPath = FindApp();
            if (appPath == null)
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                Arguments = "\"" + filePath + "\"",
                UseShellExecute = false
            });

            return true;
        }

        /// <summary>
        /// Tries to send a file path to an already-running instance via named pipe.
        /// Returns true if the message was delivered.
        /// </summary>
        private static bool TrySendToRunningInstance(string filePath)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1000); // 1 second timeout
                    using (var writer = new StreamWriter(client))
                    {
                        writer.WriteLine(filePath);
                        writer.Flush();
                    }
                }
                return true;
            }
            catch
            {
                // Pipe not available — app isn't running
                return false;
            }
        }

        private static string FindApp()
        {
            // 1. Check registry
            string registryPath = GetRegistryPath();
            if (registryPath != null)
                return registryPath;

            // 2. Check PATH
            string pathResult = FindOnPath();
            if (pathResult != null)
                return pathResult;

            // 3. Check common install locations
            string commonPath = FindInCommonLocations();
            if (commonPath != null)
                return commonPath;

            return null;
        }

        private static string GetRegistryPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key != null)
                    {
                        var value = key.GetValue(RegistryValue) as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            var exePath = Path.Combine(value, ExeName);
                            if (File.Exists(exePath))
                                return exePath;
                        }
                    }
                }
            }
            catch
            {
                // Registry access might fail
            }

            return null;
        }

        private static string FindOnPath()
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
                return null;

            foreach (var dir in pathVar.Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), ExeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Invalid path entry
                }
            }

            return null;
        }

        private static string FindInCommonLocations()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var candidates = new[]
            {
                Path.Combine(localAppData, "Programs", "SQLPerformanceStudio", ExeName),
                Path.Combine(localAppData, "Programs", "plan-b", ExeName),
                Path.Combine(programFiles, "DarlingData", "SQLPerformanceStudio", ExeName),
                Path.Combine(programFilesX86, "DarlingData", "SQLPerformanceStudio", ExeName),
                Path.Combine(programFiles, "SQL Performance Studio", ExeName),
                Path.Combine(programFilesX86, "SQL Performance Studio", ExeName),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Shows a file picker so the user can locate the app manually.
        /// Saves the chosen path to the registry for next time.
        /// Returns the exe path, or null if the user cancelled.
        /// </summary>
        public static string BrowseForApp()
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "Locate SQL Performance Studio";
                dialog.Filter = "SQL Performance Studio|PlanViewer.App.exe|All executables|*.exe";
                dialog.FileName = ExeName;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    && File.Exists(dialog.FileName))
                {
                    // Save the directory to registry so we find it automatically next time
                    SaveRegistryPath(Path.GetDirectoryName(dialog.FileName));
                    return dialog.FileName;
                }
            }

            return null;
        }

        private static void SaveRegistryPath(string directory)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
                {
                    key?.SetValue(RegistryValue, directory);
                }
            }
            catch
            {
                // Best effort — registry write might fail
            }
        }
    }
}
