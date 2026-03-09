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
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;

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
    private async void CopyMcpCommand_Click(object? sender, RoutedEventArgs e)
    {
        var port = int.TryParse(McpPortInput.Text, out var p) && p >= 1024 && p <= 65535 ? p : 5152;
        var command = $"claude mcp add --transport streamable-http --scope user performance-studio http://localhost:{port}/";
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(command);
            McpCopyStatus.Text = "Copied to clipboard!";
        }
    }

    private string? _updateUrl;

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        UpdateLink.IsVisible = false;

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var result = await UpdateChecker.CheckAsync(currentVersion);

        if (result.Error != null)
        {
            UpdateStatusText.Text = $"Error: {result.Error}";
        }
        else if (result.UpdateAvailable)
        {
            UpdateStatusText.Text = $"New version available:";
            UpdateLink.Text = result.LatestVersion;
            UpdateLink.IsVisible = true;
            _updateUrl = result.ReleaseUrl;
        }
        else
        {
            UpdateStatusText.Text = $"You're up to date ({result.LatestVersion})";
        }

        CheckUpdateButton.IsEnabled = true;
    }

    private void UpdateLink_Click(object? sender, PointerPressedEventArgs e)
    {
        if (_updateUrl != null) OpenUrl(_updateUrl);
    }

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
