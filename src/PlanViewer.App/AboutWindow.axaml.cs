/*
 * Performance Studio — SQL Server Execution Plan Analyzer
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PlanViewer.App.Mcp;

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

        // Load current MCP settings
        var settings = McpSettings.Load();
        McpEnabledCheckBox.IsChecked = settings.Enabled;
        McpPortInput.Text = settings.Port.ToString();

        // Save on change
        McpEnabledCheckBox.IsCheckedChanged += (_, _) => SaveMcpSettings();
        McpPortInput.LostFocus += (_, _) => SaveMcpSettings();
    }

    private void SaveMcpSettings()
    {
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".planview");
        var settingsFile = Path.Combine(settingsDir, "settings.json");

        var json = JsonSerializer.Serialize(new
        {
            mcp_enabled = McpEnabledCheckBox.IsChecked == true,
            mcp_port = int.TryParse(McpPortInput.Text, out var p) && p >= 1024 && p <= 65535 ? p : 5152
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(settingsFile, json);
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
