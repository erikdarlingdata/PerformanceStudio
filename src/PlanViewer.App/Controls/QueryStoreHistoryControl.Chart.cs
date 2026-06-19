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
	// ── Chart ────────────────────────────────────────────────────────────

	private void UpdateChart()
	{
		HistoryChart.Plot.Clear();
		_scatters.Clear();
		_selectionRect = null;
		_highlightMarkers.Clear();
		_avgLine = null;
		_highlightedPlanHash = null;

		if (_historyData.Count == 0)
		{
			HistoryChart.Refresh();
			return;
		}

		var selected = MetricSelector.SelectedItem as ComboBoxItem;
		var tag = selected?.Tag?.ToString() ?? "AvgCpuMs";
		var label = selected?.Content?.ToString() ?? "Avg CPU (ms)";

		var planGroups = _historyData
			.GroupBy(r => r.QueryPlanHash)
			.OrderBy(g => g.Key)
			.ToList();

		foreach (var group in planGroups)
		{
			var planHash = group.Key;
			var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);

			var ordered = group.OrderBy(r => r.IntervalStartUtc).ToList();
			var xs = ordered.Select(r => TimeDisplayHelper.ConvertForDisplay(r.IntervalStartUtc).ToOADate()).ToArray();
			var ys = ordered.Select(r => GetMetricValue(r, tag)).ToArray();

			var scatter = HistoryChart.Plot.Add.Scatter(xs, ys);
			scatter.Color = color.WithAlpha(140);
			scatter.LegendText = "";
			scatter.LineWidth = 2;
			scatter.MarkerSize = 8;
			scatter.MarkerShape = MarkerShape.FilledCircle;
			scatter.MarkerLineColor = ScottPlot.Color.FromHex("#AAAAAA");
			scatter.MarkerLineWidth = 1f;

			_scatters.Add((scatter, planHash.Length > 10 ? planHash[..10] : planHash, planHash));
		}

		var allValues = _historyData.Select(r => GetMetricValue(r, tag)).ToArray();
		if (allValues.Length > 0)
		{
			var avg = allValues.Average();
			_avgLine = HistoryChart.Plot.Add.HorizontalLine(avg);
			_avgLine.Color = ScottPlot.Color.FromHex("#FFD54F").WithAlpha(150);
			_avgLine.LineWidth = 2f;
			_avgLine.LinePattern = LinePattern.DenselyDashed;
			_avgLine.Text = $"avg: {avg:N0}";
			_avgLine.LabelFontColor = ScottPlot.Color.FromHex("#E4E6EB");
			_avgLine.LabelFontSize = 11;
			_avgLine.LabelBackgroundColor = ScottPlot.Color.FromHex("#333333").WithAlpha(170);
			_avgLine.LabelOppositeAxis = false;
			_avgLine.LabelRotation = 0;
			_avgLine.LabelAlignment = Alignment.LowerLeft;
			_avgLine.LabelOffsetX = 38;
			_avgLine.LabelOffsetY = -8;
		}

		HistoryChart.Plot.Axes.AutoScale();
		var yLimits = HistoryChart.Plot.Axes.GetLimits();
		HistoryChart.Plot.Axes.SetLimitsY(0, yLimits.Top * 1.1);

		HistoryChart.Plot.HideLegend();

		ConfigureSmartXAxis();

		HistoryChart.Plot.YLabel(label);
		ApplyDarkTheme();
		HistoryChart.Refresh();
	}

	private void ConfigureSmartXAxis()
	{
		if (_historyData.Count == 0) return;

		var minTime = _historyData.Min(r => r.IntervalStartUtc);
		var maxTime = _historyData.Max(r => r.IntervalStartUtc);
		var span = maxTime - minTime;

		HistoryChart.Plot.Axes.DateTimeTicksBottom();

		if (span.TotalHours <= 48)
		{
			HistoryChart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#E4E6EB");
			HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
			{
				LabelFormatter = dt => dt.ToString("HH:mm\nMM/dd")
			};
		}
		else if (span.TotalDays <= 14)
		{
			HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
			{
				LabelFormatter = dt => dt.ToString("HH:mm\nMM/dd")
			};
		}
		else
		{
			HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
			{
				LabelFormatter = dt => dt.ToString("MM/dd\nyyyy")
			};
		}
	}

	// ── Dot highlighting on chart ────────────────────────────────────────

	private void ClearHighlightMarkers()
	{
		foreach (var m in _highlightMarkers)
			HistoryChart.Plot.Remove(m);
		_highlightMarkers.Clear();
	}

	private void HighlightDotsOnChart(HashSet<int> rowIndices)
	{
		ClearHighlightMarkers();
		if (rowIndices.Count == 0)
		{
			HistoryChart.Refresh();
			return;
		}

		var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";

		var groups = rowIndices
			.Where(i => i >= 0 && i < _historyData.Count)
			.Select(i => _historyData[i])
			.GroupBy(r => r.QueryPlanHash);

		foreach (var group in groups)
		{
			var color = _planHashColorMap.GetValueOrDefault(group.Key, PlanColors[0]);
			var xs = group.Select(r => TimeDisplayHelper.ConvertForDisplay(r.IntervalStartUtc).ToOADate()).ToArray();
			var ys = group.Select(r => GetMetricValue(r, tag)).ToArray();

			var highlight = HistoryChart.Plot.Add.Scatter(xs, ys);
			highlight.LineWidth = 0;
			highlight.MarkerSize = 14;
			highlight.MarkerShape = MarkerShape.FilledCircle;
			highlight.Color = color;
			highlight.MarkerLineColor = ScottPlot.Colors.White;
			highlight.MarkerLineWidth = 2.5f;

			_highlightMarkers.Add(highlight);
		}

		HistoryChart.Refresh();
	}

}
