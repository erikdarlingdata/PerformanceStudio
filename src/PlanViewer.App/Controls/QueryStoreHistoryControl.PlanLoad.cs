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
	/// <summary>
	/// Returns the plan hash of the currently selected row(s), or null if none.
	/// </summary>
	private string? GetSelectedPlanHash()
	{
		// From grid selection
		if (HistoryDataGrid.SelectedItem is QueryStoreHistoryRow row)
			return row.QueryPlanHash;

		// From chart selection
		if (_selectedRowIndices.Count > 0)
		{
			var idx = _selectedRowIndices.First();
			if (idx >= 0 && idx < _historyData.Count)
				return _historyData[idx].QueryPlanHash;
		}

		return null;
	}

	private ContextMenu CreatePlanContextMenu()
	{
		var loadFirstItem = new MenuItem { Header = "Load Oldest Plan for This Hash" };
		var loadLastItem = new MenuItem { Header = "Load Newest Plan for This Hash" };

		loadFirstItem.Click += (_, _) => LoadPlanFromSelection(oldest: true);
		loadLastItem.Click += (_, _) => LoadPlanFromSelection(oldest: false);

		var menu = new ContextMenu
		{
			Items = { loadFirstItem, loadLastItem }
		};

		menu.Opening += (_, _) =>
		{
			var hasSelection = GetSelectedPlanHash() != null;
			foreach (var item in menu.Items.OfType<MenuItem>())
				item.IsEnabled = hasSelection;
		};

		return menu;
	}

	private void BuildContextMenu()
	{
		HistoryDataGrid.ContextMenu = CreatePlanContextMenu();
		HistoryChart.ContextMenu = CreatePlanContextMenu();
	}

	private async void LoadPlanFromSelection(bool oldest)
	{
		if (_isLoadingPlan) return;
		var planHash = GetSelectedPlanHash();
		if (string.IsNullOrEmpty(planHash)) return;

		_isLoadingPlan = true;
		StatusText.Text = "Loading plan…";
		try
		{
			var plan = await QueryStoreService.FetchPlanByHashAsync(
				_connectionString, planHash, oldest);

			if (plan == null || string.IsNullOrEmpty(plan.PlanXml))
			{
				StatusText.Text = "Plan not found";
				return;
			}

			StatusText.Text = _dataSummaryText;
			PlanLoadRequested?.Invoke(this, new HistoryPlanLoadEventArgs(plan));
		}
		catch (Exception ex)
		{
			StatusText.Text = $"Error loading plan: {ex.Message}";
		}
		finally
		{
			_isLoadingPlan = false;
		}
	}
}
