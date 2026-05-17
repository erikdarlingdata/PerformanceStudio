using System;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

/// <summary>
/// Event args for when a plan is loaded from the history context menu.
/// </summary>
public class HistoryPlanLoadEventArgs : EventArgs
{
	public QueryStorePlan Plan { get; }

	public HistoryPlanLoadEventArgs(QueryStorePlan plan)
	{
		Plan = plan;
	}
}
