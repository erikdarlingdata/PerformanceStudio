using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class QueryStoreGridControl : UserControl
{
    /// <summary>
    /// Server-side Query Store filter, accumulated from the filter panel, the toolbar
    /// "Search by" box, and promoted column filters. Projected to chips for display and to
    /// a <see cref="QueryStoreFilter"/> for fetching.
    /// </summary>
    private readonly QueryStoreServerFilterState _serverFilterState = new();

    /// <summary>
    /// Commits every panel input to the filter state as one transaction, then re-fetches.
    /// All four id boxes are parsed first; if any is malformed the state is left untouched
    /// and no fetch happens, so a typo never partially applies.
    /// </summary>
    private async void ApplyServerFilters_Click(object? sender, RoutedEventArgs e)
    {
        long[]? includeQueryIds, ignoreQueryIds, includePlanIds, ignorePlanIds;
        try
        {
            includeQueryIds = QueryStoreFilter.ParseIdList(IncludeQueryIdsBox.Text);
            ignoreQueryIds  = QueryStoreFilter.ParseIdList(IgnoreQueryIdsBox.Text);
            includePlanIds  = QueryStoreFilter.ParseIdList(IncludePlanIdsBox.Text);
            ignorePlanIds   = QueryStoreFilter.ParseIdList(IgnorePlanIdsBox.Text);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        _serverFilterState.SetMinExecutions((long?)MinExecBox.Value);
        _serverFilterState.SetMinCpuMs((long?)MinCpuBox.Value);
        _serverFilterState.SetMinDurationMs((long?)MinDurBox.Value);
        _serverFilterState.SetQueryTextSearch(TextSearchBox.Text);
        _serverFilterState.SetQueryTextSearchNot(TextExcludeBox.Text);
        _serverFilterState.SetEscapeBrackets(EscapeBracketsBox.IsChecked == true);
        _serverFilterState.SetIncludeQueryIds(includeQueryIds);
        _serverFilterState.SetIgnoreQueryIds(ignoreQueryIds);
        _serverFilterState.SetIncludePlanIds(includePlanIds);
        _serverFilterState.SetIgnorePlanIds(ignorePlanIds);

        RebuildChips();
        await FetchPlansForRangeAsync();
    }

    /// <summary>Clears all server filters, resets the panel inputs, and re-fetches.</summary>
    private async void ClearServerFilters_Click(object? sender, RoutedEventArgs e)
    {
        _serverFilterState.Clear();
        ResetServerFilterPanelInputs();
        RebuildChips();
        await FetchPlansForRangeAsync();
    }

    /// <summary>Removes the single criterion behind the clicked chip, clears its matching panel input, and re-fetches.</summary>
    private async void ChipRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ServerFilterKind kind)
        {
            _serverFilterState.Remove(kind);
            ResetPanelInputForKind(kind);
            RebuildChips();
            await FetchPlansForRangeAsync();
        }
    }

    /// <summary>Reprojects the active criteria to chips and shows the strip only when at least one is active.</summary>
    private void RebuildChips()
    {
        var chips = _serverFilterState.GetActiveChips();
        ChipItems.ItemsSource = chips;
        ChipStrip.IsVisible = chips.Count > 0;
    }

    /// <summary>Persists the panel's expanded/collapsed state whenever the user toggles it.</summary>
    private void ServerFilterExpander_StateChanged(object? sender, RoutedEventArgs e)
    {
        var s = AppSettingsService.Load().Clone();
        s.QueryStoreFilterPanelExpanded = ServerFilterExpander.IsExpanded;
        AppSettingsService.Save(s);
    }

    /// <summary>Resets every input in the server-filter panel to its empty state.</summary>
    private void ResetServerFilterPanelInputs()
    {
        MinExecBox.Value = null;
        MinCpuBox.Value = null;
        MinDurBox.Value = null;
        TextSearchBox.Text = "";
        TextExcludeBox.Text = "";
        EscapeBracketsBox.IsChecked = false;
        IncludeQueryIdsBox.Text = "";
        IgnoreQueryIdsBox.Text = "";
        IncludePlanIdsBox.Text = "";
        IgnorePlanIdsBox.Text = "";
    }

    /// <summary>Resets the single panel input backing a criterion. Search-by-only kinds have no panel input and are skipped.</summary>
    private void ResetPanelInputForKind(ServerFilterKind kind)
    {
        switch (kind)
        {
            case ServerFilterKind.MinExecutions: MinExecBox.Value = null; break;
            case ServerFilterKind.MinCpuMs: MinCpuBox.Value = null; break;
            case ServerFilterKind.MinDurationMs: MinDurBox.Value = null; break;
            case ServerFilterKind.QueryTextSearch: TextSearchBox.Text = ""; break;
            case ServerFilterKind.QueryTextSearchNot: TextExcludeBox.Text = ""; break;
            case ServerFilterKind.IncludeQueryIds: IncludeQueryIdsBox.Text = ""; break;
            case ServerFilterKind.IgnoreQueryIds: IgnoreQueryIdsBox.Text = ""; break;
            case ServerFilterKind.IncludePlanIds: IncludePlanIdsBox.Text = ""; break;
            case ServerFilterKind.IgnorePlanIds: IgnorePlanIdsBox.Text = ""; break;
            // QueryId, PlanId, QueryHash, QueryPlanHash, Module, ExecutionType are
            // search-by-only criteria with no dedicated panel input — nothing to reset.
        }
    }

    /// <summary>
    /// Maps a grid column id to the server-filter criterion it can be promoted to, or null
    /// when the column has no server equivalent. Total CPU/Duration are intentionally not
    /// mapped: a total must never promote onto an average threshold.
    /// </summary>
    private static ServerFilterKind? MapColumnToServerKind(string columnId) => columnId switch
    {
        "Executions"  => ServerFilterKind.MinExecutions,
        "AvgCpu"      => ServerFilterKind.MinCpuMs,
        "AvgDuration" => ServerFilterKind.MinDurationMs,
        "QueryId"     => ServerFilterKind.QueryId,
        "PlanId"      => ServerFilterKind.PlanId,
        "QueryHash"   => ServerFilterKind.QueryHash,
        "PlanHash"    => ServerFilterKind.QueryPlanHash,
        "ModuleName"  => ServerFilterKind.Module,
        "QueryText"   => ServerFilterKind.QueryTextSearch,
        _             => null,
    };

    /// <summary>
    /// Promotes a client-side column filter to the matching server criterion when the
    /// filter's operator is compatible with that criterion's class. On an incompatible
    /// operator the state is left untouched and the user is told why; on success the
    /// criterion is committed and the grid re-fetched.
    /// </summary>
    private async void PromoteColumnFilterToServer(string columnId, ColumnFilterState state)
    {
        if (MapColumnToServerKind(columnId) is not { } kind)
            return;

        const string incompatibleMessage =
            "This filter's operator can't be used to search the server for this column";

        switch (kind)
        {
            // Numeric thresholds: only > / >=. Floor to long so promoted rows always qualify.
            case ServerFilterKind.MinExecutions:
            case ServerFilterKind.MinCpuMs:
            case ServerFilterKind.MinDurationMs:
                if (state.Operator is not (FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual))
                {
                    StatusText.Text = incompatibleMessage;
                    return;
                }
                if (!double.TryParse(state.Value, out var value))
                    return;
                var threshold = (long)Math.Floor(value);
                switch (kind)
                {
                    case ServerFilterKind.MinExecutions: _serverFilterState.SetMinExecutions(threshold); break;
                    case ServerFilterKind.MinCpuMs: _serverFilterState.SetMinCpuMs(threshold); break;
                    case ServerFilterKind.MinDurationMs: _serverFilterState.SetMinDurationMs(threshold); break;
                }
                break;

            // Ids: only equality.
            case ServerFilterKind.QueryId:
            case ServerFilterKind.PlanId:
                if (state.Operator is not FilterOperator.Equals)
                {
                    StatusText.Text = incompatibleMessage;
                    return;
                }
                if (!long.TryParse(state.Value, out var id))
                    return;
                if (kind == ServerFilterKind.QueryId) _serverFilterState.SetQueryId(id);
                else _serverFilterState.SetPlanId(id);
                break;

            // Hashes, module, and query text: equality or contains, on the raw value.
            case ServerFilterKind.QueryHash:
            case ServerFilterKind.QueryPlanHash:
            case ServerFilterKind.Module:
            case ServerFilterKind.QueryTextSearch:
                if (state.Operator is not (FilterOperator.Equals or FilterOperator.Contains))
                {
                    StatusText.Text = incompatibleMessage;
                    return;
                }
                switch (kind)
                {
                    case ServerFilterKind.QueryHash: _serverFilterState.SetQueryHash(state.Value); break;
                    case ServerFilterKind.QueryPlanHash: _serverFilterState.SetQueryPlanHash(state.Value); break;
                    case ServerFilterKind.Module:
                        // Default to the dbo schema when none is given, matching the toolbar search.
                        _serverFilterState.SetModule(state.Value.Contains('.') ? state.Value : $"dbo.{state.Value}");
                        break;
                    case ServerFilterKind.QueryTextSearch: _serverFilterState.SetQueryTextSearch(state.Value); break;
                }
                break;

            default:
                return;
        }

        RebuildChips();
        await FetchPlansForRangeAsync();
    }

    /// <summary>Closes the column-filter popup and promotes its filter to a server criterion.</summary>
    private void OnSearchServerRequested(object? sender, FilterAppliedEventArgs e)
    {
        _filterPopup!.IsOpen = false;
        PromoteColumnFilterToServer(e.FilterState.ColumnName, e.FilterState);
    }
}
