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
using PlanViewer.App.Helpers;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    private static string GetTabLabel(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb.Text ?? "Tab";
        if (tab.Header is string s)
            return s;
        return "Tab";
    }

    private TabItem CreateTab(string label, Control content)
    {
        var headerText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22,
            MinHeight = 22,
            Width = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Brushes.Transparent,
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = content };
        closeBtn.Tag = tab;
        closeBtn.Click += CloseTab_Click;

        // Long-press to detach, middle-click to close
        TabHeaderLongPressBehavior.Attach(
            header,
            onLongPress: () => DetachTabToWindow(tab),
            onMiddleClick: () =>
            {
                MainTabControl.Items.Remove(tab);
                UpdateEmptyOverlay();
            });

        // Right-click context menu
        var copyPathItem = new MenuItem { Header = "Copy Path", Tag = tab };
        // Only visible when tab content has a file path
        var filePath = GetTabFilePath(tab);
        copyPathItem.IsVisible = filePath != null;

        var contextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Rename Tab", Tag = new object[] { header, headerText } },
                copyPathItem,
                new Separator(),
                new MenuItem { Header = "Close", Tag = tab, InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += TabContextMenu_Click;

        header.ContextMenu = contextMenu;

        return tab;
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
        {
            MainTabControl.Items.Remove(tab);
            UpdateEmptyOverlay();
        }
    }

    private void TabContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        var headerText = item.Header?.ToString();

        switch (headerText)
        {
            case "Rename Tab":
                if (item.Tag is object[] parts)
                    StartRename((StackPanel)parts[0], (TextBlock)parts[1]);
                break;

            case "Copy Path":
                if (item.Tag is TabItem pathTab)
                {
                    var path = GetTabFilePath(pathTab);
                    if (path != null)
                        _ = this.Clipboard?.SetTextAsync(path);
                }
                break;

            case "Close":
                if (item.Tag is TabItem tab)
                {
                    MainTabControl.Items.Remove(tab);
                    UpdateEmptyOverlay();
                }
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                {
                    var others = MainTabControl.Items.Cast<TabItem>().Where(t => t != keepTab).ToList();
                    foreach (var t in others)
                        MainTabControl.Items.Remove(t);
                    MainTabControl.SelectedItem = keepTab;
                    UpdateEmptyOverlay();
                }
                break;

            case "Close All Tabs":
                MainTabControl.Items.Clear();
                UpdateEmptyOverlay();
                break;
        }
    }

    private static string? GetTabFilePath(TabItem tab)
    {
        // Plans opened from file are wrapped in a DockPanel with the viewer as the last child
        if (tab.Content is DockPanel dp)
        {
            foreach (var child in dp.Children)
            {
                if (child is PlanViewerControl v)
                    return v.SourceFilePath;
            }
        }
        return null;
    }

    private void StartRename(StackPanel header, TextBlock headerText)
    {
        var textBox = new TextBox
        {
            Text = headerText.Text,
            FontSize = 12,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Hide the text, show the textbox
        headerText.IsVisible = false;
        header.Children.Insert(0, textBox);
        textBox.Focus();
        textBox.SelectAll();

        void CommitRename()
        {
            var newName = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName))
                headerText.Text = newName;

            headerText.IsVisible = true;
            header.Children.Remove(textBox);
        }

        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter || ke.Key == Key.Escape)
            {
                if (ke.Key == Key.Escape)
                    textBox.Text = headerText.Text; // revert
                CommitRename();
                ke.Handled = true;
            }
        };

        textBox.LostFocus += (_, _) => CommitRename();
    }

    /// <summary>
    /// Gets query text from a PlanViewerControl — uses QueryText if set,
    /// otherwise concatenates StatementText from all parsed statements.
    /// </summary>
    private static string GetQueryTextFromPlan(PlanViewerControl viewer)
    {
        if (!string.IsNullOrEmpty(viewer.QueryText))
            return viewer.QueryText;

        if (viewer.CurrentPlan == null)
            return "";

        var statements = viewer.CurrentPlan.Batches
            .SelectMany(b => b.Statements)
            .Select(s => s.StatementText)
            .Where(t => !string.IsNullOrEmpty(t));

        return string.Join(Environment.NewLine, statements);
    }

    /// <summary>
    /// Detaches a tab's content into a standalone free-floating window.
    /// When that window is minimized or closed, the content returns to a tab.
    /// </summary>
    private void DetachTabToWindow(TabItem tab)
    {
        var content = tab.Content as Control;
        if (content == null) return;

        var label = GetTabLabel(tab);

        // Remove the tab
        MainTabControl.Items.Remove(tab);
        tab.Content = null; // detach the content from the tab
        UpdateEmptyOverlay();

        // Create a free-floating window
        var detachedWindow = new Window
        {
            Title = label,
            Width = 1280,
            Height = 800,
            MinWidth = 900,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Avalonia.Media.IBrush?)this.FindResource("BackgroundBrush") ?? Brushes.Black,
            Content = content,
            Icon = this.Icon
        };

        if (content is QueryStoreHistoryControl historyControl)
            historyControl.ShowCloseButton(false);

        // When window is closed or minimized, re-dock to tab
        bool redocked = false;

        void Redock()
        {
            if (redocked || IsShuttingDown) return;
            redocked = true;

            detachedWindow.Content = null; // detach from window

            if (content is QueryStoreHistoryControl hc)
                hc.ShowCloseButton(false);

            Dispatcher.UIThread.Post(() =>
            {
                if (IsShuttingDown) return;
                var newTab = CreateTab(label, content);
                MainTabControl.Items.Add(newTab);
                MainTabControl.SelectedItem = newTab;
                UpdateEmptyOverlay();
            });
        }

        detachedWindow.Closing += (_, _) => Redock();

        // Detect minimize → re-dock
        detachedWindow.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty &&
                detachedWindow.WindowState == WindowState.Minimized && !redocked)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Redock();
                    detachedWindow.Close();
                });
            }
        };

        detachedWindow.Show();
    }
}
