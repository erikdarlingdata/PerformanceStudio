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
	// ── Box selection ────────────────────────────────────────────────────

	private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(HistoryChart).Properties.IsLeftButtonPressed) return;

		_isDragging = true;
		_dragStartPoint = e.GetPosition(HistoryChart);

		if (_selectionRect != null)
		{
			HistoryChart.Plot.Remove(_selectionRect);
			_selectionRect = null;
		}

		e.Handled = true;
	}

	private void OnChartPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (!_isDragging) return;
		_isDragging = false;

		if (_selectionRect != null)
		{
			HistoryChart.Plot.Remove(_selectionRect);
			_selectionRect = null;
		}

		var endPoint = e.GetPosition(HistoryChart);
		var startCoords = PixelToCoordinates(_dragStartPoint);
		var endCoords = PixelToCoordinates(endPoint);

		var dx = Math.Abs(endPoint.X - _dragStartPoint.X);
		var dy = Math.Abs(endPoint.Y - _dragStartPoint.Y);

		if (dx < 5 && dy < 5)
		{
			HandleSingleClickSelection(endPoint);
		}
		else
		{
			HandleBoxSelection(startCoords, endCoords);
		}

		e.Handled = true;
	}

	private ScottPlot.Coordinates PixelToCoordinates(Point pos)
	{
		var lastRender = HistoryChart.Plot.RenderManager.LastRender.FigureRect;
		var scaleX = HistoryChart.Bounds.Width > 0
			? (float)(lastRender.Width / HistoryChart.Bounds.Width)
			: 1f;
		var scaleY = HistoryChart.Bounds.Height > 0
			? (float)(lastRender.Height / HistoryChart.Bounds.Height)
			: 1f;
		var pixel = new ScottPlot.Pixel((float)(pos.X * scaleX), (float)(pos.Y * scaleY));
		return HistoryChart.Plot.GetCoordinates(pixel);
	}

	private ScottPlot.Pixel PointToScaledPixel(Point pos)
	{
		var lastRender = HistoryChart.Plot.RenderManager.LastRender.FigureRect;
		var scaleX = HistoryChart.Bounds.Width > 0
			? (float)(lastRender.Width / HistoryChart.Bounds.Width)
			: 1f;
		var scaleY = HistoryChart.Bounds.Height > 0
			? (float)(lastRender.Height / HistoryChart.Bounds.Height)
			: 1f;
		return new ScottPlot.Pixel((float)(pos.X * scaleX), (float)(pos.Y * scaleY));
	}

	private void HandleSingleClickSelection(Point clickPoint)
	{
		if (_scatters.Count == 0) return;

		var pixel = PointToScaledPixel(clickPoint);
		var mouseCoords = HistoryChart.Plot.GetCoordinates(pixel);

		double bestDist = double.MaxValue;
		ScottPlot.DataPoint bestPoint = default;
		string bestPlanHash = "";
		bool found = false;

		foreach (var (scatter, _, planHash) in _scatters)
		{
			var nearest = scatter.Data.GetNearest(mouseCoords, HistoryChart.Plot.LastRender);
			if (!nearest.IsReal) continue;

			var nearestPixel = HistoryChart.Plot.GetPixel(
				new ScottPlot.Coordinates(nearest.X, nearest.Y));
			var d = Math.Sqrt(Math.Pow(nearestPixel.X - pixel.X, 2) + Math.Pow(nearestPixel.Y - pixel.Y, 2));

			if (d < 30 && d < bestDist)
			{
				bestDist = d;
				bestPoint = nearest;
				bestPlanHash = planHash;
				found = true;
			}
		}

		_selectedRowIndices.Clear();

		if (found)
		{
			var clickedTime = DateTime.FromOADate(bestPoint.X);
			for (int i = 0; i < _historyData.Count; i++)
			{
				var row = _historyData[i];
				var displayTime = TimeDisplayHelper.ConvertForDisplay(row.IntervalStartUtc);
				if (row.QueryPlanHash == bestPlanHash &&
					Math.Abs((displayTime - clickedTime).TotalMinutes) < 1)
				{
					_selectedRowIndices.Add(i);
				}
			}
		}

		HighlightDotsOnChart(_selectedRowIndices);
		HighlightGridRows();
	}

	private void HandleBoxSelection(ScottPlot.Coordinates start, ScottPlot.Coordinates end)
	{
		var x1 = Math.Min(start.X, end.X);
		var x2 = Math.Max(start.X, end.X);
		var y1 = Math.Min(start.Y, end.Y);
		var y2 = Math.Max(start.Y, end.Y);

		var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";
		_selectedRowIndices.Clear();

		for (int i = 0; i < _historyData.Count; i++)
		{
			var row = _historyData[i];
			var xVal = TimeDisplayHelper.ConvertForDisplay(row.IntervalStartUtc).ToOADate();
			var yVal = GetMetricValue(row, tag);

			if (xVal >= x1 && xVal <= x2 && yVal >= y1 && yVal <= y2)
				_selectedRowIndices.Add(i);
		}

		HighlightDotsOnChart(_selectedRowIndices);
		HighlightGridRows();
	}

	// ── Hover tooltip ────────────────────────────────────────────────────

	private void OnChartPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_scatters.Count == 0) { if (_tooltip != null) _tooltip.IsOpen = false; return; }

		if (_isDragging)
		{
			var currentPoint = e.GetPosition(HistoryChart);
			var startCoords = PixelToCoordinates(_dragStartPoint);
			var currentCoords = PixelToCoordinates(currentPoint);

			if (_selectionRect != null)
				HistoryChart.Plot.Remove(_selectionRect);

			var x1 = Math.Min(startCoords.X, currentCoords.X);
			var x2 = Math.Max(startCoords.X, currentCoords.X);
			var y1 = Math.Min(startCoords.Y, currentCoords.Y);
			var y2 = Math.Max(startCoords.Y, currentCoords.Y);

			_selectionRect = HistoryChart.Plot.Add.Rectangle(x1, x2, y1, y2);
			_selectionRect.FillColor = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(30);
			_selectionRect.LineColor = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(120);
			_selectionRect.LineWidth = 1;
			HistoryChart.Refresh();

			if (_tooltip != null) _tooltip.IsOpen = false;
			return;
		}

		try
		{
			var pos = e.GetPosition(HistoryChart);
			var pixel = PointToScaledPixel(pos);
			var mouseCoords = HistoryChart.Plot.GetCoordinates(pixel);

			double bestDist = double.MaxValue;
			ScottPlot.DataPoint bestPoint = default;
			string bestLabel = "";
			bool found = false;

			foreach (var (scatter, chartLabel, _) in _scatters)
			{
				var nearest = scatter.Data.GetNearest(mouseCoords, HistoryChart.Plot.LastRender);
				if (!nearest.IsReal) continue;

				var nearestPixel = HistoryChart.Plot.GetPixel(
					new ScottPlot.Coordinates(nearest.X, nearest.Y));
				double ddx = Math.Abs(nearestPixel.X - pixel.X);
				double ddy = Math.Abs(nearestPixel.Y - pixel.Y);

				if (ddx < 80 && ddy < bestDist)
				{
					bestDist = ddy;
					bestPoint = nearest;
					bestLabel = chartLabel;
					found = true;
				}
			}

			if (found && _tooltipText != null && _tooltip != null)
			{
				var time = DateTime.FromOADate(bestPoint.X);
				var metricLabel = (MetricSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
				_tooltipText.Text = $"{bestLabel}\n{metricLabel}: {bestPoint.Y:N2}\n{time:MM/dd HH:mm}";
				_tooltip.IsOpen = true;
			}
			else
			{
				if (_tooltip != null) _tooltip.IsOpen = false;
			}
		}
		catch (Exception)
		{
			if (_tooltip != null) _tooltip.IsOpen = false;
		}
	}

}
