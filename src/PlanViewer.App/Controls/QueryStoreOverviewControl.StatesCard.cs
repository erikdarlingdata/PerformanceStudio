using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
    // ── Query Store state card ──────────────────────────────────────────────
    //
    // This replaced a donut whose entire output was a ring and "7/7" in the middle. The ring
    // encoded four states in four arcs, each arc had to be clicked to find out WHICH databases
    // were in it, and the headline number counted read-only Query Stores as if they were still
    // collecting. A server has a handful of databases, not a thousand: the states fit on screen
    // as a sentence and a list, so that is what they are now.

    /// <summary>
    /// The order the state list reads in: whatever is broken first, then whatever has stopped
    /// collecting, then the healthy ones. Alphabetical inside each band.
    /// </summary>
    private static int StateRank(QueryStoreState state) => state switch
    {
        QueryStoreState.Error => 0,
        QueryStoreState.Off => 1,
        QueryStoreState.ReadOnly => 2,
        _ => 3
    };

    private static string StateLabel(QueryStoreState state) => state switch
    {
        QueryStoreState.ReadWrite => "read write",
        QueryStoreState.ReadOnly => "read only",
        QueryStoreState.Error => "error",
        _ => "off"
    };

    /// <summary>
    /// The style class that paints one state, from the triad DarkTheme documents: green confirms,
    /// amber warns, red fails. Read-only is amber, not red — it has stopped capturing but its
    /// history still reads, and on a readable secondary it is the state you asked for.
    /// </summary>
    private static string StateClass(QueryStoreState state) => state switch
    {
        QueryStoreState.ReadWrite => "healthy",
        QueryStoreState.ReadOnly => "warn",
        _ => "bad"
    };

    private void DrawStatesCard()
    {
        StatesList.Children.Clear();

        var total = _states.Count;
        var healthy = _states.Count(s => s.State == QueryStoreState.ReadWrite);
        var degraded = _states.Count(s => s.State == QueryStoreState.ReadOnly);
        var broken = total - healthy - degraded;

        StatesHeadline.Text = total == 0
            ? "No databases to report on"
            : $"Query Store healthy on {healthy} of {total} database{(total == 1 ? "" : "s")}";

        /* The headline takes the worst state below it, so the card answers "is anything wrong"
           before the reader gets to the list. Set rather than Add: this redraws on every refresh
           and a stale class would stack a second colour on the first. */
        StatesHeadline.Classes.Set("bad", broken > 0);
        StatesHeadline.Classes.Set("warn", broken == 0 && degraded > 0);
        StatesHeadline.Classes.Set("healthy", total > 0 && broken == 0 && degraded == 0);

        foreach (var db in _states
                     .OrderBy(s => StateRank(s.State))
                     .ThenBy(s => s.DatabaseName, StringComparer.OrdinalIgnoreCase))
        {
            StatesList.Children.Add(BuildStateRow(db));
        }
    }

    /// <summary>
    /// One line of the state list: database on the left, its state on the right in the colour that
    /// state earns. The database name ellipsizes and keeps its full text on hover, because this
    /// card is the narrowest column on the dashboard.
    /// </summary>
    private static Border BuildStateRow(DatabaseQueryStoreState db)
    {
        var name = new TextBlock { Text = db.DatabaseName, Classes = { "stateName" } };
        Grid.SetColumn(name, 0);

        var state = new TextBlock { Text = StateLabel(db.State), Classes = { "stateValue" } };
        state.Classes.Add(StateClass(db.State));
        Grid.SetColumn(state, 1);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { name, state }
        };

        /* Transparent, not null: a Border with no background answers the pointer only where its
           child's glyphs happen to be, so the tooltip would be unreachable in the gap between the
           name and the state. */
        var container = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 1),
            Child = row
        };

        var tip = db.DatabaseName;
        if (db.State == QueryStoreState.Error && !string.IsNullOrEmpty(db.ErrorMessage))
            tip += $"\n{db.ErrorMessage}";

        ToolTip.SetTip(container, tip);
        ToolTip.SetShowDelay(container, 200);
        return container;
    }
}
