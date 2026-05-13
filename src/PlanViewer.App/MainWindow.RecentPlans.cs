using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// Adds a file path to the recent plans list, saves settings, and rebuilds the menu.
    /// </summary>
    private void TrackRecentPlan(string filePath)
    {
        AppSettingsService.AddRecentPlan(_appSettings, filePath);
        AppSettingsService.Save(_appSettings);
        RebuildRecentPlansMenu();
    }

    /// <summary>
    /// Rebuilds the Recent Plans submenu from the current settings.
    /// Shows a disabled "(empty)" item when the list is empty, plus a Clear Recent separator.
    /// </summary>
    private void RebuildRecentPlansMenu()
    {
        RecentPlansMenu.Items.Clear();

        if (_appSettings.RecentPlans.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(empty)",
                IsEnabled = false
            };
            RecentPlansMenu.Items.Add(emptyItem);
            return;
        }

        foreach (var path in _appSettings.RecentPlans)
        {
            var fileName = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path) ?? "";

            // Show "filename  —  directory" so the user can distinguish same-named files
            var displayText = string.IsNullOrEmpty(directory)
                ? fileName
                : $"{fileName}  —  {directory}";

            var item = new MenuItem
            {
                Header = displayText,
                Tag = path
            };

            item.Click += RecentPlanItem_Click;
            RecentPlansMenu.Items.Add(item);
        }

        RecentPlansMenu.Items.Add(new Separator());

        var clearItem = new MenuItem { Header = "Clear Recent Plans" };
        clearItem.Click += ClearRecentPlans_Click;
        RecentPlansMenu.Items.Add(clearItem);
    }

    private void RecentPlanItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string path)
            return;

        if (!File.Exists(path))
        {
            // File was moved or deleted — remove from the list and notify the user
            AppSettingsService.RemoveRecentPlan(_appSettings, path);
            AppSettingsService.Save(_appSettings);
            RebuildRecentPlansMenu();

            ShowError($"The file no longer exists and has been removed from recent plans:\n\n{path}");
            return;
        }

        LoadPlanFile(path);
    }

    private void ClearRecentPlans_Click(object? sender, RoutedEventArgs e)
    {
        _appSettings.RecentPlans.Clear();
        AppSettingsService.Save(_appSettings);
        RebuildRecentPlansMenu();
    }
}
