using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    /// <summary>
    /// How many recent plans the empty state offers. The File menu lists them all; this is a
    /// starting point, not a second copy of that menu.
    /// </summary>
    private const int EmptyStateRecentPlanLimit = 5;

    /// <summary>
    /// Decides whether the editor's empty state is showing, and rebuilds it when it is.
    ///
    /// <para>Shown only when this session holds nothing at all: no text, and no sub-tab beyond
    /// the Query Editor. Both halves matter — a session whose editor is empty because the user
    /// is reading the plan they just ran must not have an overlay waiting behind that plan.</para>
    ///
    /// <para>Called from the editor's TextChanged and from the sub-tab watcher, so it re-decides
    /// in both directions: delete every character with no plan open and the panel comes back,
    /// which is the same state a fresh tab is in and deserves the same offer.</para>
    /// </summary>
    private void RefreshEmptyState()
    {
        var empty = QueryEditor.Text.Length == 0 && SubTabControl.Items.Count <= 1;

        if (empty)
        {
            /* Rebuilt on the way in rather than once at construction: the recent list changes
               while the app runs, and a session can sit empty across a dozen plans being
               opened. At most five rows, and only while the buffer is empty. */
            EmptyStateFileActions.IsVisible = _owningWindow != null;
            PopulateEmptyStateRecentPlans(_owningWindow);
        }

        EmptyStateOverlay.IsVisible = empty;
    }

    /// <summary>
    /// The window this session belongs to, or null while it has none — a session detached into
    /// its own window, where the two File menu actions below have nothing to act on.
    /// </summary>
    private MainWindow? _owningWindow;

    /// <summary>
    /// Told to the session by <see cref="MainWindow.CreateTab"/>, which every top-level session
    /// passes through exactly when it gains a tab, and unset by a detach.
    ///
    /// <para>Handed over rather than walked up to: the tree answer (<c>FindLogicalAncestorOfType</c>,
    /// as <see cref="UpdateCompareButtonState"/> uses) is only true once the session has been
    /// realised, and a tab that opens behind the selected one never is — so the empty state on
    /// the tab you have not looked at yet would be built with no recent plans in it and never
    /// rebuilt.</para>
    /// </summary>
    internal void SetOwningWindow(MainWindow? window)
    {
        _owningWindow = window;
        RefreshEmptyState();
    }

    private void PopulateEmptyStateRecentPlans(MainWindow? owner)
    {
        EmptyStateRecentPlans.Children.Clear();

        var recent = owner?.RecentPlans ?? (IReadOnlyList<string>)Array.Empty<string>();
        foreach (var path in recent.Take(EmptyStateRecentPlanLimit))
            EmptyStateRecentPlans.Children.Add(BuildRecentPlanRow(path));

        EmptyStateRecentSection.IsVisible = EmptyStateRecentPlans.Children.Count > 0;
    }

    /// <summary>
    /// One clickable recent plan: file name over its folder, the full path on hover.
    /// </summary>
    private Border BuildRecentPlanRow(string path)
    {
        /* Size and colour come from the overlay's styles via these classes, not from a resource
           lookup here: a row is built before this session has been attached to anything, and
           FindResource on an unattached control hands back UnsetValue rather than a brush. */
        var fileName = new TextBlock
        {
            Text = Path.GetFileName(path),
            Classes = { "label" },
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var directory = new TextBlock
        {
            Text = Path.GetDirectoryName(path) ?? "",
            Classes = { "path" },
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var row = new Border
        {
            /* The same hit-testing reason the XAML rows give: without a background this answers
               clicks on its glyphs only. The Classes entry supplies the hover and the padding
               from the overlay's styles. */
            Background = Brushes.Transparent,
            Classes = { "action" },
            Tag = path,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { fileName, directory }
            }
        };

        ToolTip.SetTip(row, path);
        row.PointerPressed += EmptyStateRecentPlan_PointerPressed;

        return row;
    }

    /// <summary>
    /// A click on the panel itself, rather than on one of its rows, puts the caret back where
    /// the user expects it. The editor is underneath and usually already has focus — this is
    /// for the case where something else took it.
    /// </summary>
    private void EmptyStateBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        FocusEditor();
    }

    /// <summary>
    /// Only the left button activates an action row. PointerPressed fires for every button,
    /// and a right- or middle-click on "Open a plan" opening the file picker is a surprise,
    /// not a shortcut.
    /// </summary>
    private bool IsLeftButton(PointerPressedEventArgs e, object? sender) =>
        sender is Control c && e.GetCurrentPoint(c).Properties.IsLeftButtonPressed;

    private void EmptyStateOpenPlan_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender)) return;
        e.Handled = true; // else this bubbles to the background handler above
        _owningWindow?.OpenFile_Click(this, new RoutedEventArgs());
    }

    private void EmptyStatePastePlan_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender)) return;
        e.Handled = true;
        _owningWindow?.PasteXml_Click(this, new RoutedEventArgs());
    }

    private void EmptyStateConnect_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender)) return;
        e.Handled = true;
        // The toolbar's own Connect handler, so the two entry points cannot diverge.
        Connect_Click(this, new RoutedEventArgs());
    }

    private void EmptyStateRecentPlan_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender)) return;
        e.Handled = true;

        if (sender is Border { Tag: string path })
            _owningWindow?.OpenRecentPlan(path);
    }

    private void FocusEditor()
    {
        QueryEditor.Focus();
        QueryEditor.TextArea.Focus();
    }
}
