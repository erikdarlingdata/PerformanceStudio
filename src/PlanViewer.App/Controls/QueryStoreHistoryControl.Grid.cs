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
	private void BuildColorMap()
	{
		_planHashColorMap.Clear();
		var maxPlans = Services.AppSettingsService.Load().QueryHistoryMaxPlans;
		var hashes = _historyData
			.GroupBy(r => r.QueryPlanHash)
			.OrderByDescending(g => g.Sum(r => r.CountExecutions))
			.Take(maxPlans)
			.Select(g => g.Key)
			.OrderBy(h => h)
			.ToList();
		for (int i = 0; i < hashes.Count; i++)
			_planHashColorMap[hashes[i]] = PlanColors[i % PlanColors.Length];
	}

	private void ApplyColorIndicators()
	{
		HistoryDataGrid.LoadingRow -= OnDataGridLoadingRow;
		HistoryDataGrid.LoadingRow += OnDataGridLoadingRow;
	}

	private void OnDataGridLoadingRow(object? sender, DataGridRowEventArgs e)
	{
		if (e.Row.DataContext is QueryStoreHistoryRow row &&
			_planHashColorMap.TryGetValue(row.QueryPlanHash, out var color))
		{
			var avColor = Avalonia.Media.Color.FromRgb(color.R, color.G, color.B);
			var brush = new SolidColorBrush(avColor);
			e.Row.Tag = brush;

			if (TryApplyColorIndicator(e.Row, brush))
				return;
		}

		e.Row.Loaded -= OnRowLoaded;
		e.Row.Loaded += OnRowLoaded;
	}

	private void OnRowLoaded(object? sender, RoutedEventArgs e)
	{
		if (sender is not DataGridRow dgRow) return;
		dgRow.Loaded -= OnRowLoaded;

		if (dgRow.Tag is SolidColorBrush brush)
			TryApplyColorIndicator(dgRow, brush);
	}

	private bool TryApplyColorIndicator(DataGridRow dgRow, SolidColorBrush brush)
	{
		var presenter = FindVisualChild<DataGridCellsPresenter>(dgRow);
		if (presenter == null) return false;

		var cell = presenter.Children.OfType<DataGridCell>().FirstOrDefault();
		if (cell == null) return false;

		var border = FindVisualChild<Border>(cell, "ColorIndicator");
		if (border == null) return false;

		border.Background = brush;
		return true;
	}

	private static T? FindVisualChild<T>(Avalonia.Visual parent, string? name = null) where T : Avalonia.Visual
	{
		if (parent is T t && (name == null || (t is Control c && c.Name == name)))
			return t;

		var children = parent.GetVisualChildren();
		foreach (var child in children)
		{
			if (child is Avalonia.Visual vc)
			{
				var found = FindVisualChild<T>(vc, name);
				if (found != null) return found;
			}
		}
		return null;
	}

	private void HighlightGridRows()
	{
		// Update row backgrounds directly without resetting ItemsSource
		// (which would wipe sort state and scroll position)
		foreach (var row in HistoryDataGrid.GetVisualDescendants().OfType<DataGridRow>())
		{
			var idx = row.Index;
			row.Background = _selectedRowIndices.Contains(idx)
				? new SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 79, 195, 247))
				: Brushes.Transparent;
		}

		// Also keep the LoadingRow handler for rows that get virtualized in/out
		HistoryDataGrid.LoadingRow -= OnHighlightLoadingRow;
		HistoryDataGrid.LoadingRow += OnHighlightLoadingRow;

		// Scroll to first selected row
		if (_selectedRowIndices.Count > 0)
		{
			var firstIdx = _selectedRowIndices.Min();
			if (firstIdx < _historyData.Count)
				HistoryDataGrid.ScrollIntoView(_historyData[firstIdx], null);
		}
	}

	private void OnHighlightLoadingRow(object? sender, DataGridRowEventArgs e)
	{
		var idx = e.Row.Index;
		if (_selectedRowIndices.Contains(idx))
		{
			e.Row.Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 79, 195, 247));
		}
		else
		{
			e.Row.Background = Brushes.Transparent;
		}
	}

	// ── Grid row click → chart highlight ─────────────────────────────────

	private void HistoryDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		_selectedRowIndices.Clear();
		if (HistoryDataGrid.SelectedItems != null)
		{
			foreach (var item in HistoryDataGrid.SelectedItems)
			{
				if (item is QueryStoreHistoryRow row)
				{
					var idx = _historyData.IndexOf(row); // O(n) but list is small (<500 items)
					if (idx >= 0)
						_selectedRowIndices.Add(idx);
				}
			}
		}

		HighlightDotsOnChart(_selectedRowIndices);
	}

}
