/*
 * SQL Performance Studio — SQL Server Execution Plan Analyzer
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PlanViewer.App;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/erikdarlingdata/PerformanceStudio";
    private const string IssuesUrl = "https://github.com/erikdarlingdata/PerformanceStudio/issues";
    private const string DarlingDataUrl = "https://www.erikdarling.com";

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void GitHubLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(GitHubUrl);
    private void ReportIssueLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(IssuesUrl);
    private void DarlingDataLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(DarlingDataUrl);
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            // Silently fail — nothing useful to show the user
        }
    }
}
