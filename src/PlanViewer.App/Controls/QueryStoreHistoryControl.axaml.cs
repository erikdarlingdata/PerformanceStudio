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
	private readonly string _connectionString;
	private readonly string _queryHash;
	private readonly string _database;
	private readonly string _queryText;
	private readonly DateTime? _slicerStartUtc;
	private readonly DateTime? _slicerEndUtc;
	private readonly int _maxHoursBack;
	private bool _useFullHistory;
	private CancellationTokenSource? _fetchCts;
	private List<QueryStoreHistoryRow> _historyData = new();
	private readonly List<(ScottPlot.Plottables.Scatter Scatter, string Label, string PlanHash)> _scatters = new();

	// Hover tooltip
	private Popup? _tooltip;
	private TextBlock? _tooltipText;

	// Box selection state
	private bool _isDragging;
	private Point _dragStartPoint;
	private ScottPlot.Plottables.Rectangle? _selectionRect;
	private readonly HashSet<int> _selectedRowIndices = new();

	// Highlight markers for selected dots
	private readonly List<ScottPlot.Plottables.Scatter> _highlightMarkers = new();

	// Color mapping: plan hash -> color
	private readonly Dictionary<string, ScottPlot.Color> _planHashColorMap = new();

	// Legend state
	private bool _legendExpanded;
	private bool _isLoadingPlan;
	private string _dataSummaryText = "";

	// Legend highlight: which plan hash is currently highlighted (null = none)
	private string? _highlightedPlanHash;
	private ScottPlot.Plottables.HorizontalLine? _avgLine;

	// Active button highlight brush
	private static readonly SolidColorBrush ActiveButtonBg = new(Avalonia.Media.Color.FromRgb(0x4F, 0xC3, 0xF7));
	private static readonly SolidColorBrush ActiveButtonFg = new(Avalonia.Media.Color.FromRgb(0x11, 0x12, 0x17));
	private static readonly SolidColorBrush InactiveButtonFg = new(Avalonia.Media.Color.FromRgb(0x9D, 0xA5, 0xB4));

	private static readonly ScottPlot.Color[] PlanColors =
	{
		ScottPlot.Color.FromHex("#4FC3F7"),
		ScottPlot.Color.FromHex("#FF7043"),
		ScottPlot.Color.FromHex("#66BB6A"),
		ScottPlot.Color.FromHex("#AB47BC"),
		ScottPlot.Color.FromHex("#FFA726"),
		ScottPlot.Color.FromHex("#26C6DA"),
		ScottPlot.Color.FromHex("#F06292"),
		ScottPlot.Color.FromHex("#A1887F"),
	};

	// Map grid orderBy tags to history metric tags
	private static readonly Dictionary<string, string> OrderByToMetricTag = new()
	{
		["cpu"]              = "TotalCpuMs",
		["avg-cpu"]          = "AvgCpuMs",
		["duration"]         = "TotalDurationMs",
		["avg-duration"]     = "AvgDurationMs",
		["reads"]            = "TotalLogicalReads",
		["avg-reads"]        = "AvgLogicalReads",
		["writes"]           = "TotalLogicalWrites",
		["avg-writes"]       = "AvgLogicalWrites",
		["physical-reads"]   = "TotalPhysicalReads",
		["avg-physical-reads"] = "AvgPhysicalReads",
		["memory"]           = "TotalMemoryMb",
		["avg-memory"]       = "AvgMemoryMb",
		["executions"]       = "CountExecutions",
	};

	/// <summary>
	/// Gets the query hash displayed by this control (used for tab labels).
	/// </summary>
	public string QueryHash => _queryHash;

	/// <summary>
	/// Gets the database name displayed by this control.
	/// </summary>
	public string Database => _database;

	/// <summary>
	/// Raised when the user requests to load a plan from the context menu.
	/// </summary>
	public event EventHandler<HistoryPlanLoadEventArgs>? PlanLoadRequested;

	/// <summary>
	/// Parameterless constructor required by Avalonia designer.
	/// </summary>
	public QueryStoreHistoryControl()
	{
		_connectionString = "";
		_queryHash = "";
		_database = "";
		_queryText = "";
		InitializeComponent();
	}

	public QueryStoreHistoryControl(string connectionString, string queryHash,
		string queryText, string database,
		string initialMetricTag = "AvgCpuMs",
		DateTime? slicerStartUtc = null, DateTime? slicerEndUtc = null,
		int slicerDaysBack = 30)
	{
		_connectionString = connectionString;
		_queryHash = queryHash;
		_database = database;
		_queryText = queryText;
		_slicerStartUtc = slicerStartUtc;
		_slicerEndUtc = slicerEndUtc;
		_maxHoursBack = slicerDaysBack * 24;
		InitializeComponent();

		Helpers.DataGridBehaviors.Attach(HistoryDataGrid);

		QueryIdentifierText.Text = $"Query Store History: {queryHash} in [{database}]";
		QueryTextBox.Text = queryText;

		// Select initial metric in the combo box
		var metricTag = initialMetricTag;
		foreach (var entry in MetricSelector.Items)
		{
			if (entry is ComboBoxItem item && item.Tag?.ToString() == metricTag)
			{
				MetricSelector.SelectedItem = item;
				break;
			}
		}

		// Default to range period mode when slicer range is available
		_useFullHistory = !(_slicerStartUtc.HasValue && _slicerEndUtc.HasValue);
		UpdateRangeButtons();

		// Build hover tooltip
		_tooltipText = new TextBlock
		{
			Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
			FontSize = 13
		};
		_tooltip = new Popup
		{
			PlacementTarget = HistoryChart,
			Placement = PlacementMode.Pointer,
			IsHitTestVisible = false,
			IsLightDismissEnabled = false,
			Child = new Border
			{
				Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x33, 0x33, 0x33)),
				BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x55, 0x55, 0x55)),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(3),
				Padding = new Thickness(8, 4, 8, 4),
				Child = _tooltipText
			}
		};
		((Grid)Content!).Children.Add(_tooltip);

		HistoryChart.PointerMoved += OnChartPointerMoved;
		HistoryChart.PointerExited += (_, _) => { if (_tooltip != null) _tooltip.IsOpen = false; };
		HistoryChart.PointerPressed += OnChartPointerPressed;
		HistoryChart.PointerReleased += OnChartPointerReleased;

		// Disable ScottPlot's built-in left-click-drag pan so our box selection works
		HistoryChart.UserInputProcessor.LeftClickDragPan(enable: false);

		BuildContextMenu();

		AttachedToVisualTree += async (_, _) =>
		{
			if (_historyData.Count == 0)
				await LoadHistoryAsync();
		};

		DetachedFromVisualTree += (_, _) => CancelFetch();
	}

	/// <summary>
	/// Shows the Close button in the footer (used when hosted in a detached window).
	/// </summary>
	public void ShowCloseButton(bool visible = true)
	{
		FooterPanel.IsVisible = visible;
	}

	/// <summary>
	/// Cancels any pending data fetch.
	/// </summary>
	public void CancelFetch()
	{
		_fetchCts?.Cancel();
		_fetchCts?.Dispose();
		_fetchCts = null;
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e)
	{
		CancelFetch();
	}

	/// <summary>
	/// Maps a grid orderBy tag (e.g. "cpu", "avg-duration") to the history metric tag.
	/// </summary>
	public static string MapOrderByToMetricTag(string orderBy)
	{
		return OrderByToMetricTag.TryGetValue(orderBy.ToLowerInvariant(), out var tag)
			? tag
			: "AvgCpuMs";
	}

	private static double GetMetricValue(QueryStoreHistoryRow row, string tag) => tag switch
	{
		"AvgCpuMs"           => row.AvgCpuMs,
		"AvgDurationMs"      => row.AvgDurationMs,
		"AvgLogicalReads"    => row.AvgLogicalReads,
		"AvgLogicalWrites"   => row.AvgLogicalWrites,
		"AvgPhysicalReads"   => row.AvgPhysicalReads,
		"AvgMemoryMb"        => row.AvgMemoryMb,
		"AvgRowcount"        => row.AvgRowcount,
		"TotalCpuMs"         => row.TotalCpuMs,
		"TotalDurationMs"    => row.TotalDurationMs,
		"TotalLogicalReads"  => row.TotalLogicalReads,
		"TotalLogicalWrites" => row.TotalLogicalWrites,
		"TotalPhysicalReads" => row.TotalPhysicalReads,
		"TotalMemoryMb"      => row.TotalMemoryMb,
		"CountExecutions"    => row.CountExecutions,
		_                    => row.AvgCpuMs,
	};

	private void ApplyDarkTheme()
	{
		var fig = ScottPlot.Color.FromHex("#22252b");
		var data = ScottPlot.Color.FromHex("#111217");
		var text = ScottPlot.Color.FromHex("#E4E6EB");
		var grid = ScottPlot.Colors.White.WithAlpha(40);

		HistoryChart.Plot.FigureBackground.Color = fig;
		HistoryChart.Plot.DataBackground.Color = data;
		HistoryChart.Plot.Axes.Color(text);
		HistoryChart.Plot.Grid.MajorLineColor = grid;
		HistoryChart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = text;
		HistoryChart.Plot.Axes.Left.TickLabelStyle.ForeColor = text;
	}

	private void UpdateRangeButtons()
	{
		if (_useFullHistory)
		{
			FullHistoryButton.Background = ActiveButtonBg;
			FullHistoryButton.Foreground = ActiveButtonFg;
			RangePeriodButton.Background = Brushes.Transparent;
			RangePeriodButton.Foreground = InactiveButtonFg;
		}
		else
		{
			RangePeriodButton.Background = ActiveButtonBg;
			RangePeriodButton.Foreground = ActiveButtonFg;
			FullHistoryButton.Background = Brushes.Transparent;
			FullHistoryButton.Foreground = InactiveButtonFg;
		}
	}

	private async void RangePeriod_Click(object? sender, RoutedEventArgs e)
	{
		if (!_useFullHistory) return;
		_useFullHistory = false;
		UpdateRangeButtons();
		await LoadHistoryAsync();
	}

	private async void FullHistory_Click(object? sender, RoutedEventArgs e)
	{
		if (_useFullHistory) return;
		_useFullHistory = true;
		UpdateRangeButtons();
		await LoadHistoryAsync();
	}

	private void MetricSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (IsVisible && _historyData.Count > 0)
			UpdateChart();
	}

	private async void CopyQuery_Click(object? sender, RoutedEventArgs e)
	{
		if (string.IsNullOrEmpty(_queryText)) return;
		var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
		if (clipboard != null)
			await clipboard.SetTextAsync(_queryText);
	}

	private void Close_Click(object? sender, RoutedEventArgs e)
	{
		// When in a detached window, close it (this destroys the history view)
		CancelFetch();
		var window = TopLevel.GetTopLevel(this) as Window;
		if (window != null && window is not PlanViewer.App.MainWindow)
			window.Close();
	}

}
