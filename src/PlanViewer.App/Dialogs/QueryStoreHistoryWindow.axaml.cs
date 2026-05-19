using System;
using Avalonia.Controls;
using PlanViewer.App.Controls;

namespace PlanViewer.App.Dialogs;

public partial class QueryStoreHistoryWindow : Window
{
	public QueryStoreHistoryControl? HistoryControlInstance { get; private set; }

	/// <summary>
	/// Designer-only constructor.
	/// </summary>
	public QueryStoreHistoryWindow()
	{
		InitializeComponent();
	}

	public QueryStoreHistoryWindow(string connectionString, string queryHash,
		string queryText, string database,
		string initialMetricTag = "AvgCpuMs",
		DateTime? slicerStartUtc = null, DateTime? slicerEndUtc = null,
		int slicerDaysBack = 30)
	{
		InitializeComponent();

		var control = new QueryStoreHistoryControl(
			connectionString, queryHash, queryText, database,
			initialMetricTag, slicerStartUtc, slicerEndUtc, slicerDaysBack);
		control.ShowCloseButton(true);
		Content = control;
		HistoryControlInstance = control;

		Title = $"Query Store History: {queryHash} in [{database}]";
	}

	/// <summary>
	/// Maps a grid orderBy tag (e.g. "cpu", "avg-duration") to the history metric tag.
	/// </summary>
	public static string MapOrderByToMetricTag(string orderBy)
		=> QueryStoreHistoryControl.MapOrderByToMetricTag(orderBy);

	protected override void OnClosed(EventArgs e)
	{
		HistoryControlInstance?.CancelFetch();
		base.OnClosed(e);
	}
}
