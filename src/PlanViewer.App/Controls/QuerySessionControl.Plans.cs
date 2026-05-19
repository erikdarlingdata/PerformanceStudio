using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private void AddPlanTab(string planXml, string queryText, bool estimated, string? labelOverride = null)
    {
        _planCounter++;
        var label = labelOverride ?? (estimated ? $"Est Plan {_planCounter}" : $"Plan {_planCounter}");

        var viewer = new PlanViewerControl();
        viewer.Metadata = _serverMetadata;
        viewer.ConnectionString = _connectionString;
        viewer.SetConnectionServices(_credentialService, _connectionStore);
        if (_serverConnection != null)
            viewer.SetConnectionStatus(_serverConnection.ServerName, _selectedDatabase);
        viewer.OpenInEditorRequested += OnOpenInEditorRequested;
        viewer.LoadPlan(planXml, label, queryText);

        // Build tab header with close button and right-click rename
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
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = viewer };
        closeBtn.Tag = tab;
        closeBtn.Click += ClosePlanTab_Click;

        // Right-click context menu
        var contextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Rename Tab", Tag = new object[] { header, headerText } },
                new Separator(),
                new MenuItem { Header = "Close", Tag = tab, InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += PlanTabContextMenu_Click;

        header.ContextMenu = contextMenu;

        SubTabControl.Items.Add(tab);
        SubTabControl.SelectedItem = tab;
        UpdateCompareButtonState();
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
                    textBox.Text = headerText.Text;
                CommitRename();
                ke.Handled = true;
            }
        };

        textBox.LostFocus += (_, _) => CommitRename();
    }

    private void ClosePlanTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
        {
            if (tab.Content is PlanViewerControl viewer)
                viewer.Clear();
            SubTabControl.Items.Remove(tab);
            UpdateCompareButtonState();
        }
    }

    private void PlanTabContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        switch (item.Header?.ToString())
        {
            case "Rename Tab":
                if (item.Tag is object[] parts)
                    StartRename((StackPanel)parts[0], (TextBlock)parts[1]);
                break;

            case "Close":
                if (item.Tag is TabItem tab)
                {
                    if (tab.Content is PlanViewerControl closeViewer)
                        closeViewer.Clear();
                    SubTabControl.Items.Remove(tab);
                    UpdateCompareButtonState();
                }
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                {
                    // Keep the Editor tab (index 0) and the selected tab
                    var others = SubTabControl.Items.Cast<object>()
                        .OfType<TabItem>()
                        .Where(t => t != keepTab && t.Content is PlanViewerControl)
                        .ToList();
                    foreach (var t in others)
                    {
                        if (t.Content is PlanViewerControl otherViewer)
                            otherViewer.Clear();
                        SubTabControl.Items.Remove(t);
                    }
                    SubTabControl.SelectedItem = keepTab;
                    UpdateCompareButtonState();
                }
                break;

            case "Close All Tabs":
                var planTabs = SubTabControl.Items.Cast<object>()
                    .OfType<TabItem>()
                    .Where(t => t.Content is PlanViewerControl)
                    .ToList();
                foreach (var t in planTabs)
                {
                    if (t.Content is PlanViewerControl allViewer)
                        allViewer.Clear();
                    SubTabControl.Items.Remove(t);
                }
                SubTabControl.SelectedIndex = 0; // back to Editor
                UpdateCompareButtonState();
                break;
        }
    }

    private void UpdateCompareButtonState()
    {
        int planCount = 0;
        foreach (var item in SubTabControl.Items)
        {
            if (item is TabItem t && t.Content is PlanViewerControl v && v.CurrentPlan != null)
                planCount++;
        }
        ComparePlansButton.IsEnabled = planCount >= 2;
    }

    private static string GetTabLabel(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb.Text ?? "Plan";
        if (tab.Header is string s)
            return s;
        return "Plan";
    }

    private void ComparePlans_Click(object? sender, RoutedEventArgs e)
    {
        var planTabs = GetPlanTabs().ToList();
        if (planTabs.Count < 2)
        {
            SetStatus("Need at least 2 plan tabs to compare");
            return;
        }

        ShowComparePickerDialog(planTabs);
    }

    private void ShowComparePickerDialog(List<(string label, PlanViewerControl viewer)> planTabs)
    {
        var items = planTabs.Select(t => t.label).ToList();

        var comboA = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            Width = 200,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var comboB = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = items.Count > 1 ? 1 : 0,
            Width = 200,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var compareBtn = new Button
        {
            Content = "Compare",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        void UpdateCompareEnabled()
        {
            compareBtn.IsEnabled = comboA.SelectedIndex >= 0 && comboB.SelectedIndex >= 0
                && comboA.SelectedIndex != comboB.SelectedIndex;
        }

        comboA.SelectionChanged += (_, _) => UpdateCompareEnabled();
        comboB.SelectionChanged += (_, _) => UpdateCompareEnabled();
        UpdateCompareEnabled();

        var rowA = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock { Text = "Plan A:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Width = 55 },
                comboA
            }
        };

        var rowB = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "Plan B:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Width = 55 },
                comboB
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            Children = { compareBtn, cancelBtn }
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Select two plans to compare:", FontSize = 14, Margin = new Avalonia.Thickness(0, 0, 0, 12) },
                rowA,
                rowB,
                buttonPanel
            }
        };

        var dialog = new Window
        {
            Title = "Compare Plans",
            Width = 380,
            Height = 220,
            MinWidth = 380,
            MinHeight = 220,
            Icon = GetParentWindow().Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        compareBtn.Click += (_, _) =>
        {
            var idxA = comboA.SelectedIndex;
            var idxB = comboB.SelectedIndex;
            if (idxA < 0 || idxB < 0 || idxA == idxB) return;

            var (labelA, viewerA) = planTabs[idxA];
            var (labelB, viewerB) = planTabs[idxB];

            var analysisA = ResultMapper.Map(viewerA.CurrentPlan!, "query editor", _serverMetadata);
            var analysisB = ResultMapper.Map(viewerB.CurrentPlan!, "query editor", _serverMetadata);

            var comparison = ComparisonFormatter.Compare(analysisA, analysisB, labelA, labelB);
            dialog.Close();
            ShowAdviceWindow("Plan Comparison", comparison);
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(GetParentWindow());
    }

    /// <summary>
    /// Gets the PlanViewerControl for the currently selected plan tab, or null if
    /// the Editor tab or no plan tab is selected.
    /// </summary>
    private PlanViewerControl? GetSelectedPlanViewer()
    {
        if (SubTabControl.SelectedItem is TabItem tab && tab.Content is PlanViewerControl viewer
            && viewer.CurrentPlan != null)
        {
            return viewer;
        }
        return null;
    }

    /// <summary>
    /// Enables or disables buttons that require a plan tab to be selected.
    /// Called when the SubTabControl selection changes and after plan tabs are added/removed.
    /// </summary>
    private void UpdatePlanTabButtonState()
    {
        var hasPlanTab = GetSelectedPlanViewer() != null;
        var hasConnection = _connectionString != null && _selectedDatabase != null;

        CopyReproButton.IsEnabled = hasPlanTab;
        GetActualPlanButton.IsEnabled = hasPlanTab && hasConnection;

        // Advice buttons also depend on a plan being selected
        HumanAdviceButton.IsEnabled = hasPlanTab;
        RobotAdviceButton.IsEnabled = hasPlanTab;
    }
}
