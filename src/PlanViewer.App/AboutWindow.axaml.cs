/*
 * Performance Studio — SQL Server Execution Plan Analyzer
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    }

    private void GitHubLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(GitHubUrl);
    private void ReportIssueLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(IssuesUrl);
    private void DarlingDataLink_Click(object? sender, PointerPressedEventArgs e) => OpenUrl(DarlingDataUrl);

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

    /* Every branch of UpdateLink_Click awaits — a dialog, the unsaved-work walk, a download —
       and the link stays clickable the whole time, so a second click would start a second
       concurrent copy of whichever step is in flight (two restart dialogs, two walks prompting
       about the same tabs, two downloads). One latch at the top covers all of them. */
    private bool _updateActionInFlight;

    private async void UpdateLink_Click(object? sender, PointerPressedEventArgs e)
    {
        if (_updateActionInFlight)
            return;
        _updateActionInFlight = true;
        try
        {
            await HandleUpdateLinkClickAsync();
        }
        finally
        {
            _updateActionInFlight = false;
        }
    }

    private async Task HandleUpdateLinkClickAsync()
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
                /* ApplyUpdatesAndRestart kills the process outright: MainWindow.OnClosing
                   never fires, so the unsaved-changes walk (#462/#473) never ran on this
                   route and dirty edits were discarded without a question — and OnClosed's
                   session save never ran either, so the relaunched app restored nothing
                   (RestoreOpenPlans had already cleared the saved list at startup). Ask the
                   same questions the close path asks, and if anyone answers Cancel, abort
                   the restart and leave this window usable — the update stays downloaded. */
                /* Resolved through the application lifetime, not Owner: an Owner-typed check
                   would fail OPEN — shown with any other owner, the walk silently vanishes and
                   this route is right back to discarding dirty edits, the exact bug being
                   fixed. The main window owns every session, so if none exists there is no
                   unsaved work to lose and restarting without a walk is genuinely safe. */
                var main = Owner as MainWindow
                    ?? (Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                        ?.MainWindow as MainWindow;

                if (main != null)
                {
                    if (!await main.ConfirmAllUnsavedWorkAsync())
                        return;

                    /* After the walk, not before: a Save answer in the walk can give a
                       scratch tab a file, which this then writes down for the restore. */
                    main.PersistSessionForRestart();
                }

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
