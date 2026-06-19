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
	// ── Legend ────────────────────────────────────────────────────────────

	private void PopulateLegendPanel()
	{
		LegendItemsPanel.Children.Clear();
		foreach (var (hash, color) in _planHashColorMap.OrderBy(kv => kv.Key))
		{
			var avColor = Avalonia.Media.Color.FromRgb(color.R, color.G, color.B);
			var item = new StackPanel
			{
				Orientation = Avalonia.Layout.Orientation.Horizontal,
				Spacing = 6,
				Tag = hash,
				Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
			};
			item.Children.Add(new Border
			{
				Width = 12, Height = 12,
				CornerRadius = new CornerRadius(2),
				Background = new SolidColorBrush(avColor),
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
			});
			item.Children.Add(new TextBlock
			{
				Text = hash,
				FontSize = 11,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
			});
			item.PointerPressed += OnLegendItemClicked;
			LegendItemsPanel.Children.Add(item);
		}
	}

	private void OnLegendItemClicked(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not StackPanel panel || panel.Tag is not string planHash) return;

		if (_highlightedPlanHash == planHash)
			_highlightedPlanHash = null;
		else
			_highlightedPlanHash = planHash;

		ApplyPlanHighlight();
		UpdateLegendVisuals();
	}

	private void UpdateLegendVisuals()
	{
		foreach (var child in LegendItemsPanel.Children)
		{
			if (child is not StackPanel panel || panel.Tag is not string hash) continue;
			var isActive = _highlightedPlanHash == null || _highlightedPlanHash == hash;
			panel.Opacity = isActive ? 1.0 : 0.4;
		}
	}

	private void ApplyPlanHighlight()
	{
		var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";

		foreach (var (scatter, _, planHash) in _scatters)
		{
			if (_highlightedPlanHash == null)
			{
				var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
				scatter.Color = color.WithAlpha(140);
				scatter.LineWidth = 2;
				scatter.MarkerSize = 8;
			}
			else if (planHash == _highlightedPlanHash)
			{
				var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
				scatter.Color = color.WithAlpha(220);
				scatter.LineWidth = 4;
				scatter.MarkerSize = 10;
			}
			else
			{
				var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
				scatter.Color = color.WithAlpha(40);
				scatter.LineWidth = 1;
				scatter.MarkerSize = 5;
			}
		}

		if (_avgLine != null)
		{
			var relevantRows = _highlightedPlanHash != null
				? _historyData.Where(r => r.QueryPlanHash == _highlightedPlanHash).ToList()
				: _historyData;

			if (relevantRows.Count > 0)
			{
				var avg = relevantRows.Select(r => GetMetricValue(r, tag)).Average();
				_avgLine.Y = avg;
				_avgLine.Text = $"avg: {avg:N0}";
				_avgLine.IsVisible = true;
			}
			else
			{
				_avgLine.IsVisible = false;
			}
		}

		HistoryChart.Refresh();
	}

	private void LegendToggle_Click(object? sender, RoutedEventArgs e)
	{
		_legendExpanded = !_legendExpanded;
		LegendPanel.IsVisible = _legendExpanded;
		LegendArrow.Text = _legendExpanded ? "\u25b2" : "\u25bc";
	}

}
