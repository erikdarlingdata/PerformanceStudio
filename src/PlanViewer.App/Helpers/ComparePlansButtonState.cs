using Avalonia.Controls;

namespace PlanViewer.App.Helpers;

/// <summary>
/// The shared state of every Compare Plans button in the app.
///
/// <para>There are two of them — one in each query session's toolbar, one in the toolbar over
/// every window-level plan tab — and both open the same window-wide picker, so both have to
/// agree about whether there is anything to pick. The plan-tab button did not: it was born
/// enabled and stayed enabled, and clicking it with fewer than two plans open ran
/// <see cref="MainWindow.ShowCompareDialog"/>'s early return, which does nothing and says
/// nothing. A button that answers a click with silence reads as a broken feature.</para>
///
/// <para>Kept in one place so the disabled explanation cannot drift between the two toolbars.
/// Named ...State rather than ComparePlansButton because the session's own button is an
/// <c>x:Name</c>d field by that name, and a type sharing it would be shadowed exactly where it
/// is used.</para>
/// </summary>
internal static class ComparePlansButtonState
{
    /// <summary>Marks a code-built Compare button so the window can find it again to refresh.</summary>
    internal const string Name = "ComparePlansButton";

    internal const string EnabledTip = "Compare any two plans open in this window";

    /// <summary>
    /// What the button says when it cannot be clicked. The disabled state is only informative if
    /// it names the thing that would fix it.
    /// </summary>
    internal const string DisabledTip = "Open a second plan to compare";

    /// <summary>
    /// Enables or disables one Compare button, and tells it why.
    /// </summary>
    internal static void Apply(Button button, bool comparable)
    {
        button.IsEnabled = comparable;
        ToolTip.SetTip(button, comparable ? EnabledTip : DisabledTip);
    }
}
