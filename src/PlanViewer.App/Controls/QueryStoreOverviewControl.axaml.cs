using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
    private readonly ServerConnection _serverConnection;
    private readonly ICredentialService _credentialService;
    private readonly string _masterConnectionString;
    private readonly int _maxDop;
    private readonly int _topN;
    private readonly bool _supportsWaitStats;
    private CancellationTokenSource? _cts;

    private List<DatabaseQueryStoreState> _states = new();
    private List<DatabaseMetrics> _metrics = new();
    private List<DatabaseTimeSlice> _timeSlices = new();
    private List<DatabaseWaitAmountTimeSlice> _waitSlices = new();
    private List<string> _activeDbs = new();

    private DateTime _slicerStartUtc;
    private DateTime _slicerEndUtc;
    private int _daysBack = 30;

    // Color palette for databases — loaded from user settings
    private readonly Color[] _palette;

    private static readonly Color OthersColor = Color.Parse("#555555");

    // Donut colors
    private static readonly Color ReadWriteColor = Color.Parse("#2EAEF1");  // light blue
    private static readonly Color ReadOnlyColor = Color.Parse("#1A5276");   // dark blue
    private static readonly Color OffColor = Color.Parse("#666666");        // grey
    private static readonly Color ErrorColor = Color.Parse("#E74C3C");      // red

    public class DrillDownEventArgs(string database, DateTime startUtc, DateTime endUtc) : EventArgs
    {
        public string Database { get; } = database;
        public DateTime StartUtc { get; } = startUtc;
        public DateTime EndUtc { get; } = endUtc;
    }

    public event EventHandler<DrillDownEventArgs>? DrillDownRequested;

    public QueryStoreOverviewControl(ServerConnection serverConnection,
        ICredentialService credentialService, int maxDop = 8, int? topN = null, bool supportsWaitStats = true)
    {
        _serverConnection = serverConnection;
        _credentialService = credentialService;
        _masterConnectionString = serverConnection.GetConnectionString(credentialService, "master");
        _maxDop = maxDop;

        var userSettings = AppSettingsService.Load();
        _topN = topN ?? userSettings.MultiQsTopDbCount;
        _palette = userSettings.MultiQsTopDbColors
            .Select(hex => { try { return Color.Parse(hex); } catch { return Color.Parse("#555555"); } })
            .ToArray();
        if (_palette.Length == 0)
            _palette = AppSettingsService.DefaultTopDbColors.Select(hex => Color.Parse(hex)).ToArray();

        _supportsWaitStats = supportsWaitStats;
        _slicerEndUtc = DateTime.UtcNow;
        _slicerStartUtc = _slicerEndUtc.AddHours(-24);

        InitializeComponent();

        this.SizeChanged += (_, _) =>
        {
            DrawDonut();
            DrawWaitStatsChart();
        };

        OverviewTimeSlicer.RangeChanged += OnSlicerRangeChanged;

        this.DetachedFromVisualTree += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = true);

        try
        {
            // Phase 1: Get states
            _states = await QueryStoreOverviewService.FetchAllStatesAsync(
                _masterConnectionString, _maxDop, ct);

            await Dispatcher.UIThread.InvokeAsync(DrawDonut);

            // Phase 2: Get time slices for active databases (cache the list)
            _activeDbs = _states
                .Where(s => s.State == QueryStoreState.ReadWrite || s.State == QueryStoreState.ReadOnly)
                .Select(s => s.DatabaseName).ToList();

            if (_activeDbs.Count == 0) return;

            _timeSlices = await QueryStoreOverviewService.FetchAllTimeSlicesAsync(
                _masterConnectionString, _activeDbs, _daysBack, _maxDop, ct);

            // Consolidate time slices across databases into QueryStoreTimeSlice for the slicer
            var consolidated = _timeSlices
                .GroupBy(s => s.IntervalStartUtc)
                .Select(g => new QueryStoreTimeSlice
                {
                    IntervalStartUtc = g.Key,
                    TotalCpu = g.Sum(x => x.TotalCpu),
                    TotalDuration = g.Sum(x => x.TotalDuration),
                    TotalReads = g.Sum(x => x.TotalReads),
                    TotalWrites = g.Sum(x => x.TotalWrites),
                    TotalPhysicalReads = g.Sum(x => x.TotalPhysicalReads),
                    TotalMemory = g.Sum(x => x.TotalMemory),
                    TotalExecutions = g.Sum(x => x.TotalExecutions),
                })
                .OrderBy(x => x.IntervalStartUtc)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
                OverviewTimeSlicer.LoadData(consolidated, "cpu"));

            // Phase 3: Metrics and wait stats for selected time range
            await RefreshMetricsAndWaitStatsAsync(ct);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = false);
        }
    }

    private async Task RefreshMetricsAndWaitStatsAsync(CancellationToken ct)
    {
        if (_activeDbs.Count == 0) return;

        await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = true);
        try
        {
            _metrics = await QueryStoreOverviewService.FetchAllMetricsAsync(
                _masterConnectionString, _activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);

            if (_supportsWaitStats)
            {
                var (slices, errors) = await QueryStoreOverviewService.FetchAllWaitStatsWithErrorsAsync(
                    _masterConnectionString, _activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);
                _waitSlices = slices;

                await Dispatcher.UIThread.InvokeAsync(() => UpdateWaitStatsWarning(errors));
            }
            else
            {
                _waitSlices.Clear();
                await Dispatcher.UIThread.InvokeAsync(() =>
                    UpdateWaitStatsWarning(new List<(string Database, string Error)>()));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DrawBarCards();
                DrawWaitStatsChart();
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = false);
        }
    }

    private void UpdateWaitStatsWarning(List<(string Database, string Error)> errors)
    {
        if (errors.Count == 0)
        {
            WaitStatsWarning.IsVisible = false;
            ToolTip.SetTip(WaitStatsWarning, null);
            return;
        }

        var header = errors.Count == 1
            ? "Wait stats incomplete (1 error):"
            : $"Wait stats incomplete ({errors.Count} errors):";
        var msg = string.Join("\n", errors.Select(e => $"[{e.Database}] {e.Error}"));

        ToolTip.SetTip(WaitStatsWarning, $"{header}\n{msg}");
        ToolTip.SetShowDelay(WaitStatsWarning, 200);
        WaitStatsWarning.IsVisible = true;
    }

    // ── Time Slicer (delegates to TimeRangeSlicerControl) ──────────────────

    private async void OnSlicerRangeChanged(object? sender, TimeRangeChangedEventArgs e)
    {
        _slicerStartUtc = e.StartUtc;
        _slicerEndUtc = e.EndUtc;

        // Don't dispose the previous CTS — the in-flight refresh still holds its token.
        // Cancel signals the previous run; GC reclaims the source after both runs unwind.
        _cts?.Cancel();
        var newCts = new CancellationTokenSource();
        _cts = newCts;

        ClearRefreshError();
        try
        {
            await RefreshMetricsAndWaitStatsAsync(newCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowRefreshError(ex);
        }
    }

    private void ShowRefreshError(Exception ex)
    {
        ToolTip.SetTip(RefreshErrorBadge, $"Last refresh failed:\n{ex.Message}");
        ToolTip.SetShowDelay(RefreshErrorBadge, 200);
        RefreshErrorBadge.IsVisible = true;
    }

    private void ClearRefreshError()
    {
        RefreshErrorBadge.IsVisible = false;
        ToolTip.SetTip(RefreshErrorBadge, null);
    }

}
