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
using Avalonia.LogicalTree;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Helpers;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private bool AddPlanTab(string planXml, string queryText, bool estimated, string? labelOverride = null)
        => AddPlanTab(planXml, queryText, estimated, labelOverride, out _);

    private bool AddPlanTab(string planXml, string queryText, bool estimated, string? labelOverride, out string? failure)
    {
        failure = null;
        _planCounter++;
        var label = labelOverride ?? (estimated ? $"Est Plan {_planCounter}" : $"Plan {_planCounter}");

        var viewer = new PlanViewerControl();
        // Sub-tab of this session: the session's toolbar above it owns the connection (#U5).
        viewer.HostedInSession = true;
        viewer.Metadata = _serverMetadata;
        viewer.ConnectionString = _connectionString;
        viewer.SetConnectionServices(_credentialService, _connectionStore);
        if (_serverConnection != null)
            viewer.SetConnectionStatus(_serverConnection.ServerName, _selectedDatabase);
        viewer.OpenInEditorRequested += OnOpenInEditorRequested;

        if (!viewer.LoadPlan(planXml, label, queryText))
        {
            // Blank XML or a parse failure. Don't navigate away from the current view
            // (e.g. the Query Store grid) to a blank tab — surface why and stay put.
            viewer.OpenInEditorRequested -= OnOpenInEditorRequested;
            failure = $"Couldn't load {label}: {viewer.LastLoadError}";
            SetErrorStatus(failure);
            return false;
        }

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
            Foreground = ForegroundToken,
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
                /* Ctrl+F4, not the Ctrl+W this label used to refuse to show. Ctrl+W stays the
                   window's: its tunnel handler claims that keystroke whenever a top-level tab is
                   selected, which is always, and closes the whole session — so a Ctrl+W label here
                   would have been a lie the user discovers by losing everything they had open.
                   F4 has no tunnel case, so it reaches the session's own handler, where it closes
                   exactly this document. The gesture is display-only in Avalonia: the binding that
                   makes it true lives in OnKeyDown, and the two only ever change together. */
                new MenuItem
                {
                    Header = "Close",
                    Tag = tab,
                    InputGesture = new KeyGesture(Key.F4, KeyModifiers.Control)
                },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += PlanTabContextMenu_Click;

        header.ContextMenu = contextMenu;

        AddDocument(tab);
        SelectDocument(tab);
        return true;
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

    /// <summary>
    /// Closes one document: lets go of whatever it was holding, then takes it out of the strip.
    /// </summary>
    /// <remarks>
    /// Said once because there are four doors onto it — the header's ✕, the context menu's Close,
    /// its two bulk siblings, and Ctrl+F4 — and the release half is the half that goes missing.
    /// A plan viewer holds an MCP session registration that nothing else unregisters, so a close
    /// that only removes the tab leaks it, invisibly and for the life of the process. The sub-tab
    /// kinds built by <see cref="CreateSubTab"/> release themselves instead, on detach, which is
    /// what removing them from the strip causes.
    /// </remarks>
    private void CloseDocument(TabItem tab)
    {
        if (tab.Content is PlanViewerControl viewer)
            viewer.Clear();

        RemoveDocument(tab);
    }

    private void ClosePlanTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
            CloseDocument(tab);
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
                    CloseDocument(tab);
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                {
                    // Keep the selected tab; the editor is not this menu's to close
                    var others = DocumentTabs
                        .Where(t => t != keepTab && t.Content is PlanViewerControl)
                        .ToList();
                    foreach (var t in others)
                        CloseDocument(t);
                    SelectDocument(keepTab);
                }
                break;

            case "Close All Tabs":
                var planTabs = DocumentTabs
                    .Where(t => t.Content is PlanViewerControl)
                    .ToList();
                foreach (var t in planTabs)
                    CloseDocument(t);
                SelectEditor();
                break;
        }
    }

    /// <summary>
    /// #447: asks the window, not this session. Comparing the plan from one query against the plan
    /// from another is the ordinary case, and counting only this session's own tabs left the button
    /// disabled in both — the reporter had to save a plan and reopen it to get at a comparison the
    /// app could already do.
    ///
    /// <para>Called by the <see cref="TabContentWatcher"/> wired to this session's sub-tabs, and by
    /// <see cref="MainWindow.DetachTabToWindow"/> when this session leaves the tab strip — the
    /// watcher only fires on sub-tab changes, and detaching changes none, so without that call the
    /// button froze at whatever the window-wide count last said until the next plan landed. It used
    /// to be called by hand at the five places that add or remove a plan tab, which is why the
    /// paths that instead fill in an existing tab — every executed query — never reached it.</para>
    /// </summary>
    internal void UpdateCompareButtonState()
    {
        /* Logical tree, not TopLevel.GetTopLevel. A TabControl realises the selected tab's content
           and nothing else, so a session sitting in a background tab has no visual root and cannot
           see its own window — and a query started in one tab and left to run while the user works
           in another lands its plan in exactly that state. GetTopLevel returned null there and the
           fallback below silently reinstated the bug this method exists to fix. The logical parent
           chain holds whether the tab is on screen or not. */
        if (this.FindLogicalAncestorOfType<MainWindow>() is { } owner)
        {
            /* Refreshes every session, not just this one: a plan appearing here can be the second
               plan that makes Compare available over THERE. */
            owner.RefreshComparePlanAvailability();
            return;
        }

        /* No owning window — the control is being hosted somewhere else or is not attached yet.
           Fall back to what this session can see rather than leaving the button in a stale state. */
        SetCompareAvailability(CountOwnPlans() >= 2);
    }

    internal void SetCompareAvailability(bool enabled) =>
        Helpers.ComparePlansButtonState.Apply(ComparePlansButton, enabled);

    private int CountOwnPlans()
    {
        int planCount = 0;
        foreach (var t in DocumentTabs)
        {
            if (t.Content is PlanViewerControl v && v.CurrentPlan != null)
                planCount++;
        }
        return planCount;
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
        /* #447: hand off to the window's picker, which lists plans from every session and labels
           them "Query 1 > Plan". This session's own picker cannot see the other query's plan, which
           is the whole complaint. */
        if (TopLevel.GetTopLevel(this) is MainWindow owner)
        {
            owner.ShowCompareDialog();
            return;
        }

        var planTabs = GetPlanTabs().ToList();
        if (planTabs.Count < 2)
        {
            SetErrorStatus("Need at least 2 plans open to compare");
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
            Background = BackgroundToken,
            Foreground = ForegroundToken,
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

            var analysisA = ResultMapper.Map(viewerA.CurrentPlan!, "query editor", _serverMetadata, viewerA.QueryText);
            var analysisB = ResultMapper.Map(viewerB.CurrentPlan!, "query editor", _serverMetadata, viewerB.QueryText);

            dialog.Close();
            ComparisonWindow.Show(GetParentWindow(), analysisA, analysisB, labelA, labelB);
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
        if (SelectedDocument is { Content: PlanViewerControl viewer } && viewer.CurrentPlan != null)
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
