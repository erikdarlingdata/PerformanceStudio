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
    private void NewQuery_Click(object? sender, RoutedEventArgs e)
    {
        _queryCounter++;
        var label = $"Query {_queryCounter}";

        var session = new QuerySessionControl(_credentialService, _connectionStore);
        var tab = CreateTab(label, session);

        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL Server Execution Plans")
                {
                    Patterns = new[] { "*.sqlplan" }
                },
                new FilePickerFileType("SQL Scripts")
                {
                    Patterns = new[] { "*.sql" }
                },
                new FilePickerFileType("XML Files")
                {
                    Patterns = new[] { "*.xml" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                OpenFileByExtension(path);
        }
    }

    private async void PasteXml_Click(object? sender, RoutedEventArgs e)
    {
        await PasteXmlAsync();
    }

    private static bool IsSupportedFile(string? path)
    {
        return path != null && _supportedExtensions.Any(ext =>
            path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null && files.Any(f => IsSupportedFile(f.TryGetLocalPath())))
                e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (IsSupportedFile(path))
                OpenFileByExtension(path!);
        }
    }

    /// <summary>
    /// Opens one or more files by path. Used by the macOS activation handler when a
    /// plan is double-clicked in Finder (the path arrives via an event, not argv).
    /// Marshals to the UI thread and skips paths that no longer exist.
    /// </summary>
    public void OpenFiles(IEnumerable<string> paths)
    {
        void OpenAll()
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    OpenFileByExtension(path);
            }
            Activate();
        }

        if (Dispatcher.UIThread.CheckAccess())
            OpenAll();
        else
            Dispatcher.UIThread.Post(OpenAll);
    }

    private void OpenFileByExtension(string filePath)
    {
        if (filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            LoadSqlFile(filePath);
        else
            LoadPlanFile(filePath);
    }

    private void LoadSqlFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var fileName = Path.GetFileName(filePath);

            _queryCounter++;
            var session = new QuerySessionControl(_credentialService, _connectionStore);
            session.QueryEditor.Text = text;

            var tab = CreateTab(fileName, session);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();
        }
        catch (Exception ex)
        {
            var dialog = new Window
            {
                Title = "Error Opening File",
                Width = 450,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Failed to open: {Path.GetFileName(filePath)}",
                            FontWeight = FontWeight.Bold,
                            Margin = new Avalonia.Thickness(0, 0, 0, 10)
                        },
                        new TextBlock
                        {
                            Text = ex.Message,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            dialog.ShowDialog(this);
        }
    }

    private void LoadPlanFile(string filePath)
    {
        try
        {
            var xml = File.ReadAllText(filePath);

            // SSMS saves plans as UTF-16 with encoding="utf-16" in the XML declaration.
            // File.ReadAllText auto-detects the BOM, but the resulting C# string still
            // contains encoding="utf-16" which causes XDocument.Parse to fail.
            xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var fileName = Path.GetFileName(filePath);

            if (!ValidatePlanXml(xml, fileName))
                return;

            var viewer = new PlanViewerControl();
            viewer.SetConnectionServices(_credentialService, _connectionStore);
            viewer.LoadPlan(xml, fileName);
            viewer.SourceFilePath = filePath;

            // Wrap viewer with advice toolbar
            var content = CreatePlanTabContent(viewer);

            var tab = CreateTab(fileName, content);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();

            // Track in recent plans list and persist
            TrackRecentPlan(filePath);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to open {Path.GetFileName(filePath)}:\n\n{ex.Message}");
        }
    }

    private async Task PasteXmlAsync()
    {
        var clipboard = this.Clipboard;
        if (clipboard == null) return;

        var xml = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(xml))
        {
            ShowError("The clipboard does not contain any text.");
            return;
        }

        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        if (!ValidatePlanXml(xml, "Pasted Plan"))
            return;

        var viewer = new PlanViewerControl();
        viewer.SetConnectionServices(_credentialService, _connectionStore);
        viewer.LoadPlan(xml, "Pasted Plan");

        var content = CreatePlanTabContent(viewer);
        var tab = CreateTab("Pasted Plan", content);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private bool ValidatePlanXml(string xml, string label)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            if (doc.Root?.Name.LocalName != "ShowPlanXML" &&
                doc.Descendants(ns + "ShowPlanXML").FirstOrDefault() == null)
            {
                ShowError($"{label}: XML is valid but does not appear to be a SQL Server execution plan.\n\nExpected root element: ShowPlanXML");
                return false;
            }
            return true;
        }
        catch (System.Xml.XmlException ex)
        {
            ShowError($"{label}: The XML is not valid.\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves the file paths of all currently open file-based plan tabs.
    /// </summary>
    private void SaveOpenPlans()
    {
        _appSettings.OpenPlans.Clear();

        foreach (var item in MainTabControl.Items)
        {
            if (item is not TabItem tab) continue;

            var path = GetTabFilePath(tab);
            if (!string.IsNullOrEmpty(path))
                _appSettings.OpenPlans.Add(path);
        }

        AppSettingsService.Save(_appSettings);
    }

    /// <summary>
    /// Restores plan tabs from the previous session. Skips files that no longer exist.
    /// Falls back to a new query tab if nothing was restored.
    /// </summary>
    private void RestoreOpenPlans()
    {
        var restored = false;

        foreach (var path in _appSettings.OpenPlans)
        {
            if (File.Exists(path))
            {
                LoadPlanFile(path);
                restored = true;
            }
        }

        // Clear the open plans list now that we've restored
        _appSettings.OpenPlans.Clear();
        AppSettingsService.Save(_appSettings);

        if (!restored)
        {
            // Nothing to restore — open a fresh query editor like before
            NewQuery_Click(this, new RoutedEventArgs());
        }
    }
}
