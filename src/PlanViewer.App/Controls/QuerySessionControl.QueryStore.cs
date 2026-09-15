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
using Avalonia.LogicalTree;
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
    private bool HasQueryStoreTab()
    {
        return DocumentTabs.Any(t => t.Content is QueryStoreGridControl);
    }

    public void TriggerQueryStore() => QueryStore_Click(null, new RoutedEventArgs());

    /// <summary>
    /// Creates a sub-tab with a standard header (label + optional extra buttons + close button).
    /// Returns the TabItem. The close button removes the tab from the document strip.
    /// </summary>
    private TabItem CreateSubTab(string label, Control content, Action<TabItem>? onClose = null, params Button[] extraButtons)
    {
        var headerText = new TextBlock
        {
            Text = label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 12
        };

        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = ForegroundToken,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Background = Brushes.Transparent
        };
        header.Children.Add(headerText);
        foreach (var btn in extraButtons)
            header.Children.Add(btn);
        header.Children.Add(closeBtn);

        var tab = new TabItem { Header = header, Content = content };
        closeBtn.Tag = tab;
        closeBtn.Click += (s, _) =>
        {
            if (s is Button btn && btn.Tag is TabItem t)
            {
                onClose?.Invoke(t);
                RemoveDocument(t);
            }
        };

        return tab;
    }

    /// <summary>Gets the header TextBlock from a sub-tab created via CreateSubTab.</summary>
    private static TextBlock? GetSubTabHeaderText(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb;
        return null;
    }

    /// <summary>
    /// The toolbar's way in to the Overview. It is a view rather than a document now, so this and
    /// the view bar's Overview segment are the same gesture and share one implementation — two
    /// entry points that could disagree about what "open the Overview" means is exactly the bug
    /// nobody finds.
    /// </summary>
    private async void QueryStoreOverview_Click(object? sender, RoutedEventArgs e)
    {
        await ShowOverviewAsync();
    }

    private async Task OpenQueryStoreForDatabaseAsync(string database, DateTime? initialStartUtc = null, DateTime? initialEndUtc = null)
    {
        var connStr = _serverConnection!.GetConnectionString(_credentialService, database);

        // Check if Query Store is enabled
        SetStatus($"Checking Query Store on {database}...");
        try
        {
            var (enabled, state, readOnlyReplica) = await QueryStoreService.CheckEnabledAsync(connStr);
            if (!enabled)
            {
                SetErrorStatus(readOnlyReplica
                    ? $"{database} is a read-only replica with no Query Store data ({state ?? "unknown"}); enable it on the primary"
                    : $"Query Store not enabled on {database} ({state ?? "unknown"})");
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatusFromException(ex);
            return;
        }

        ClearStatus();

        // Check if wait stats are supported
        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        if (supportsWaitStats)
        {
            try
            {
                supportsWaitStats = await QueryStoreService.IsWaitStatsCaptureEnabledAsync(connStr);
            }
            catch { supportsWaitStats = false; }
        }

        var databases = DatabaseBox.Items.OfType<string>().ToList();

        var grid = new QueryStoreGridControl(_serverConnection!, _credentialService,
            database, databases, supportsWaitStats);
        if (initialStartUtc.HasValue && initialEndUtc.HasValue)
            grid.SetInitialTimeRange(initialStartUtc.Value, initialEndUtc.Value);
        grid.PlansSelected += OnQueryStorePlansSelected;

        var tab = CreateSubTab($"Query Store — {database}", grid);
        grid.DatabaseChanged += (_, db) =>
        {
            if (GetSubTabHeaderText(tab) is TextBlock tb)
                tb.Text = $"Query Store — {db}";
        };

        AddDocument(tab);
        SelectDocument(tab);
    }

    private async void QueryStore_Click(object? sender, RoutedEventArgs e)
    {
        // If a QS tab already exists, always show connection dialog for a fresh tab
        if (HasQueryStoreTab() || _connectionString == null || _selectedDatabase == null)
        {
            await ShowConnectionDialogAsync();
            if (_connectionString == null || _selectedDatabase == null)
                return;
        }

        // Check if Query Store is enabled
        SetStatus("Checking Query Store...");
        try
        {
            var (enabled, state, readOnlyReplica) = await QueryStoreService.CheckEnabledAsync(_connectionString);
            if (!enabled)
            {
                SetErrorStatus(readOnlyReplica
                    ? $"Read-only replica with no Query Store data ({state ?? "unknown"}); enable it on the primary"
                    : $"Query Store not enabled ({state ?? "unknown"})");
                return;
            }
        }
        catch (Exception ex)
        {
            /* Was cut to 80 characters here before being handed to a status bar that already does
               TextTrimming="CharacterEllipsis". The trimming is the bar's job and it does it against
               the actual available width; doing it again in code just threw away text the control
               would have kept, and with it the tooltip that now carries the full message. Same
               family as #448. */
            SetStatusFromException(ex);
            return;
        }

        ClearStatus();

        // Check if wait stats are supported (SQL 2017+ / Azure) and capture is enabled
        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        if (supportsWaitStats)
        {
            try
            {
                var connStr = _serverConnection!.GetConnectionString(_credentialService, _selectedDatabase!);
                supportsWaitStats = await QueryStoreService.IsWaitStatsCaptureEnabledAsync(connStr);
            }
            catch
            {
                supportsWaitStats = false;
            }
        }

        // Build database list from the current DatabaseBox
        var databases = DatabaseBox.Items.OfType<string>().ToList();

        var grid = new QueryStoreGridControl(_serverConnection!, _credentialService,
            _selectedDatabase!, databases, supportsWaitStats);
        grid.PlansSelected += OnQueryStorePlansSelected;

        var tab = CreateSubTab($"Query Store — {_selectedDatabase}", grid);
        // Update tab header when database is changed via the grid's picker
        grid.DatabaseChanged += (_, db) =>
        {
            if (GetSubTabHeaderText(tab) is TextBlock tb)
                tb.Text = $"Query Store — {db}";
        };

        AddDocument(tab);
        SelectDocument(tab);
    }

    /// <summary>
    /// Opens a plan tab for each plan picked out of the Query Store grid.
    ///
    /// <para>Internal so a test can hand it plans it already has. The grid is what fetches them from
    /// a server; nothing below this line needs one, which is what makes the Query Store side of
    /// #447 testable without a live instance.</para>
    /// </summary>
    internal void OnQueryStorePlansSelected(object? sender, List<QueryStorePlan> plans)
    {
        int loaded = 0;
        var failures = new List<string>();
        foreach (var qsPlan in plans)
        {
            var tabLabel = $"QS {qsPlan.QueryId} / {qsPlan.PlanId}";
            if (AddPlanTab(qsPlan.PlanXml, qsPlan.QueryText, estimated: true, labelOverride: tabLabel, out var failure))
                loaded++;
            else if (failure != null)
                failures.Add(failure);
        }

        /* The one status that matters comes after the loop: every successful AddPlanTab selects
           its new tab, and selecting a sub-tab clears the strip — so a failure reported mid-batch
           is wiped by the very next success. Only a message written after the last tab survives. */
        if (failures.Count == 0)
            SetStatus($"{plans.Count} Query Store plans loaded");
        else if (plans.Count == 1)
            SetErrorStatus(failures[0]);
        else
            SetErrorStatus($"Loaded {loaded} of {plans.Count} Query Store plans. {string.Join(" ", failures)}");

        /* No manual button enables here: every successful AddPlanTab above selected its tab,
           whose SelectionChanged already ran UpdatePlanTabButtonState — and when EVERY plan in
           the batch failed, nothing was added or selected and the buttons must stay disabled.
           An unconditional enable at this spot lit Advice with no document at all. */
    }

    /// <summary>
    /// Adds a Query Store History control as a sub-tab in this session.
    /// Supports long-press to detach into a free-floating window.
    /// </summary>
    public void AddHistorySubTab(string label, QueryStoreHistoryControl control)
    {
        // Wire up plan load from context menu (unsubscribe first to prevent leaks on re-dock)
        control.PlanLoadRequested -= OnHistoryPlanLoadRequested;
        control.PlanLoadRequested += OnHistoryPlanLoadRequested;

        var detachBtn = new Button
        {
            Content = "↗",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = MutedToken,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Avalonia.Controls.ToolTip.SetTip(detachBtn, "Detach to Window");

        var tab = CreateSubTab(label, control,
            onClose: t => { if (t.Content is QueryStoreHistoryControl hc) hc.CancelFetch(); },
            detachBtn);

        detachBtn.Tag = tab;
        detachBtn.Click += (s, _) =>
        {
            if (s is Button btn && btn.Tag is TabItem t)
                DetachHistorySubTabToWindow(t);
        };

        AddDocument(tab);
        SelectDocument(tab);
    }

    private void OnHistoryPlanLoadRequested(object? sender, HistoryPlanLoadEventArgs e)
    {
        var plan = e.Plan;
        var tabLabel = $"QS {plan.QueryId} / {plan.PlanId}";
        AddPlanTab(plan.PlanXml, plan.QueryText, estimated: true, labelOverride: tabLabel);
    }

    /// <summary>
    /// Detaches a history sub-tab into a standalone free-floating window.
    /// Close = destroy. A Re-dock button allows explicit return to sub-tabs.
    /// </summary>
    private void DetachHistorySubTabToWindow(TabItem tab)
    {
        var content = tab.Content as QueryStoreHistoryControl;
        if (content == null) return;

        var tabLabel = GetSubTabHeaderText(tab)?.Text ?? "History";

        // Remove from sub-tabs
        RemoveDocument(tab);
        tab.Content = null;

        var mainWindow = Avalonia.Controls.TopLevel.GetTopLevel(this) as Window;

        content.ShowCloseButton(false);

        DetachedWindowHelper.ShowDetached(
            content,
            title: tabLabel,
            icon: mainWindow?.Icon,
            backgroundBrush: (Avalonia.Media.IBrush?)this.FindResource("BackgroundBrush"),
            onRedock: c =>
            {
                /* Shutdown is decided at REDOCK time through the logical tree. The visual
                   answer captured at detach goes stale, is a plain Window for a detached
                   session, and is null for an unrealized background tab - and a redock during
                   shutdown rebuilds a sub-tab inside a session that is being torn down. Null
                   here (a detached session) means no MainWindow shutdown applies; proceed. */
                if (this.FindLogicalAncestorOfType<MainWindow>() is { IsShuttingDown: true })
                    return;
                AddHistorySubTab(tabLabel, (QueryStoreHistoryControl)c);
            },
            onClosing: c =>
            {
                if (c is QueryStoreHistoryControl hc)
                    hc.CancelFetch();
            });
    }
}
