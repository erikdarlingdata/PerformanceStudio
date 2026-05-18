/*
 * Performance Studio — SQL Server Execution Plan Analyzer
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using Velopack;

namespace PlanViewer.App;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/erikdarlingdata/PerformanceStudio";
    private const string IssuesUrl = "https://github.com/erikdarlingdata/PerformanceStudio/issues";
    private const string DarlingDataUrl = "https://www.erikdarling.com";
    private const string ReleasesUrl = "https://github.com/erikdarlingdata/PerformanceStudio/releases/latest";

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";

        // Load current MCP settings
        var mcp = McpSettings.Load();
        McpEnabledCheckBox.IsChecked = mcp.Enabled;
        McpPortInput.Text = mcp.Port.ToString();

        // Save on change
        McpEnabledCheckBox.IsCheckedChanged += (_, _) => SaveMcpSettings();
        McpPortInput.LostFocus += (_, _) => SaveMcpSettings();

        // Load proxy settings. The password is intentionally NOT round-tripped
        // through the UI — TextBox.PasswordChar only masks the glyph, the cleartext
        // still lives in the visual/accessibility tree. We surface "(saved — leave
        // blank to keep)" via the watermark instead, and only update the credential
        // when the user types a new value.
        var proxy = ProxySettings.Load();
        _hasStoredProxyPassword = !string.IsNullOrEmpty(proxy.Password);
        ProxySystemRadio.IsChecked = proxy.Mode == ProxyMode.System;
        ProxyManualRadio.IsChecked = proxy.Mode == ProxyMode.Manual;
        ProxyAddressInput.Text = proxy.Address;
        ProxyUsernameInput.Text = proxy.Username;
        ProxyPasswordInput.Watermark = _hasStoredProxyPassword
            ? "(saved — leave blank to keep)"
            : "";
        ProxyManualPanel.IsVisible = proxy.Mode == ProxyMode.Manual;

        // Both radios fire IsCheckedChanged on every selection (one going false,
        // one going true). Only the now-checked one should drive the save —
        // otherwise the credential write races itself.
        void OnProxyRadioChanged(object? sender, RoutedEventArgs _)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                ProxyManualPanel.IsVisible = ProxyManualRadio.IsChecked == true;
                SaveProxySettings();
            }
        }
        ProxySystemRadio.IsCheckedChanged += OnProxyRadioChanged;
        ProxyManualRadio.IsCheckedChanged += OnProxyRadioChanged;
        ProxyAddressInput.LostFocus += (_, _) => SaveProxySettings();
        ProxyUsernameInput.LostFocus += (_, _) => SaveProxySettings();
        ProxyPasswordInput.LostFocus += (_, _) => SaveProxySettings();
    }

    private bool _hasStoredProxyPassword;

    private void SaveMcpSettings()
    {
        Services.SettingsFile.Update(o =>
        {
            o["mcp_enabled"] = McpEnabledCheckBox.IsChecked == true;
            o["mcp_port"] = int.TryParse(McpPortInput.Text, out var p) && p >= 1024 && p <= 65535 ? p : 5152;
        });
    }

    private void SaveProxySettings()
    {
        var typedPassword = ProxyPasswordInput.Text ?? "";
        var settings = new ProxySettings
        {
            Mode = ProxyManualRadio.IsChecked == true ? ProxyMode.Manual : ProxyMode.System,
            Address = ProxyAddressInput.Text ?? "",
            Username = ProxyUsernameInput.Text ?? "",
            Password = typedPassword
        };
        // Empty textbox + an existing stored password means "keep what's there".
        // Save() signals "leave the credential alone" with TouchCredential=false.
        settings.TouchCredential = !(typedPassword.Length == 0 && _hasStoredProxyPassword);
        settings.Save();
        if (settings.TouchCredential)
            _hasStoredProxyPassword = !string.IsNullOrEmpty(typedPassword);
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
    private UpdateManager? _velopackMgr;
    private UpdateInfo? _velopackUpdate;

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        UpdateLink.IsVisible = false;
        ReleasesPageLink.IsVisible = false;

        // Try Velopack first (Windows only, supports download + apply). The custom
        // downloader routes through the user's proxy + Windows credentials so this
        // works on corporate networks (issue #314).
        string? velopackError = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _velopackMgr = new UpdateManager(
                    new Velopack.Sources.GithubSource(
                        "https://github.com/erikdarlingdata/PerformanceStudio",
                        null, false, new ProxyAwareDownloader()));

                _velopackUpdate = await _velopackMgr.CheckForUpdatesAsync();
                if (_velopackUpdate != null)
                {
                    UpdateStatusText.Text = "Update available:";
                    UpdateLink.Text = $"v{_velopackUpdate.TargetFullRelease.Version} — click to install";
                    UpdateLink.IsVisible = true;
                    CheckUpdateButton.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                // Velopack packages may not exist yet — fall through to API check.
                // Hold onto the message in case the API check also fails (issue #314
                // is exactly the case where the auth error here is the useful one).
                velopackError = ex.Message;
            }
        }

        // Fallback: GitHub API check (opens browser)
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var result = await UpdateChecker.CheckAsync(currentVersion);

        if (result.Error != null)
        {
            UpdateStatusText.Text = velopackError != null && velopackError != result.Error
                ? $"Error: {result.Error} (installer check also failed: {velopackError})"
                : $"Error: {result.Error}";
            ReleasesPageLink.IsVisible = true;
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

    private void ReleasesPageLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(ReleasesUrl);

    private bool _updateDownloaded;

    private async void UpdateLink_Click(object? sender, PointerPressedEventArgs e)
    {
        // Step 3: User clicks "Restart now" after download — confirm first
        if (_updateDownloaded && _velopackMgr != null && _velopackUpdate != null)
        {
            var dialog = new Avalonia.Controls.Window
            {
                Title = "Update Ready",
                Width = 350, Height = 150,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var result = false;
            var panel = new Avalonia.Controls.StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15
            };
            panel.Children.Add(new Avalonia.Controls.TextBlock
            {
                Text = "The application will close and restart with the new version. Continue?",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            var buttonPanel = new Avalonia.Controls.StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };
            var okButton = new Avalonia.Controls.Button { Content = "Restart Now" };
            var cancelButton = new Avalonia.Controls.Button { Content = "Later" };
            okButton.Click += (_, _) => { result = true; dialog.Close(); };
            cancelButton.Click += (_, _) => { dialog.Close(); };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;

            await dialog.ShowDialog(this);

            if (result)
            {
                _velopackMgr.ApplyUpdatesAndRestart(_velopackUpdate.TargetFullRelease);
            }
            return;
        }

        // Step 2: User clicks to download
        if (_velopackMgr != null && _velopackUpdate != null)
        {
            try
            {
                UpdateLink.IsVisible = false;
                UpdateStatusText.Text = "Downloading update...";

                await _velopackMgr.DownloadUpdatesAsync(_velopackUpdate);

                _updateDownloaded = true;
                UpdateStatusText.Text = "Update downloaded.";
                UpdateLink.Text = "Restart now to apply";
                UpdateLink.IsVisible = true;
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = $"Update failed: {ex.Message}";
                UpdateLink.IsVisible = false;
                ReleasesPageLink.IsVisible = true;
            }
            return;
        }

        // Fallback: open browser
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
