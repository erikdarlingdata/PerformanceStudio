using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using ScottPlot;

namespace PlanViewer.App.Controls;

public partial class QueryStoreHistoryControl : UserControl
{
	private async System.Threading.Tasks.Task LoadHistoryAsync()
	{
		_fetchCts?.Cancel();
		_fetchCts?.Dispose();
		_fetchCts = new CancellationTokenSource();
		var ct = _fetchCts.Token;

		StatusText.Text = "Loading...";
		LoadingPanel.IsVisible = true;

		try
		{
			if (_useFullHistory)
			{
				_historyData = await QueryStoreService.FetchAggregateHistoryAsync(
					_connectionString, _queryHash, _maxHoursBack, ct);
			}
			else if (_slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
			{
				_historyData = await QueryStoreService.FetchAggregateHistoryAsync(
					_connectionString, _queryHash, ct: ct,
					startUtc: _slicerStartUtc.Value, endUtc: _slicerEndUtc.Value);
			}
			else
			{
				_historyData = await QueryStoreService.FetchAggregateHistoryAsync(
					_connectionString, _queryHash, _maxHoursBack, ct);
			}

			BuildColorMap();
			HistoryDataGrid.ItemsSource = _historyData;
			ApplyColorIndicators();

			if (_historyData.Count > 0)
			{
				var planCount = _historyData.Select(r => r.QueryPlanHash).Distinct().Count();
				var totalExec = _historyData.Sum(r => r.CountExecutions);
				var first = TimeDisplayHelper.ConvertForDisplay(_historyData.Min(r => r.IntervalStartUtc));
				var last = TimeDisplayHelper.ConvertForDisplay(_historyData.Max(r => r.IntervalStartUtc));
				StatusText.Text = $"{_historyData.Count} intervals, {planCount} plan(s), " +
								  $"{totalExec:N0} total executions | " +
								  $"{first:MM/dd HH:mm} to {last:MM/dd HH:mm}";
					_dataSummaryText = StatusText.Text;
			}
			else
			{
				StatusText.Text = "No history data found for this query.";
			}

			UpdateChart();
			PopulateLegendPanel();
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "Cancelled.";
		}
		catch (Exception ex)
		{
			StatusText.Text = ex.Message;
		}
		finally
		{
			LoadingPanel.IsVisible = false;
		}
	}

}
