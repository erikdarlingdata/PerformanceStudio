using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PlanViewer.App.Services;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Attached input behaviors for DataGrids: middle-mouse pan and guarded clipboard copy.
/// </summary>
public static class DataGridBehaviors
{
    /// <summary>Attach middle-click pan behavior to <paramref name="grid"/>.</summary>
    public static void Attach(DataGrid grid)
    {
        AttachMiddleClickPan(grid);
    }

    /// <summary>
    /// Replaces the DataGrid's built-in Ctrl+C / Ctrl+Insert copy, which awaits the
    /// clipboard unguarded inside Avalonia and can crash the app when the clipboard
    /// is locked by another process (issue #415). The tunnel handler runs before the
    /// grid's own key handling and routes the copy through ClipboardHelper instead.
    /// <paramref name="formatRow"/> turns one selected row item into its clipboard
    /// line; return null to skip the item.
    /// </summary>
    public static void AttachCopyGuard(DataGrid grid, Func<object, string?> formatRow)
    {
        grid.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            // Mirror the built-in copy's exact gate: platform command modifier
            // (Ctrl; Cmd on macOS), no Shift, no Alt.
            var commandModifiers = TopLevel.GetTopLevel(grid)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
                ?? KeyModifiers.Control;
            if (e.Key is not (Key.C or Key.Insert)
                || !e.KeyModifiers.HasFlag(commandModifiers)
                || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                return;

            // In-cell editors own their Ctrl+C (e.g. the settings grid's TextBoxes).
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) != null)
                return;

            var selected = grid.SelectedItems;
            if (selected == null || selected.Count == 0)
                return;

            // Take over whenever rows are selected so the DataGrid's unguarded
            // built-in copy never runs, even if no line ends up on the clipboard.
            e.Handled = true;

            var lines = new List<string>();
            foreach (var item in selected)
            {
                if (item != null && formatRow(item) is { } line)
                    lines.Add(line);
            }

            if (lines.Count > 0)
                _ = ClipboardHelper.TrySetTextAsync(grid, string.Join(Environment.NewLine, lines));
        }, RoutingStrategies.Tunnel);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Middle-mouse-button drag → pan (scroll) the grid
    // ─────────────────────────────────────────────────────────────────────────

    private static void AttachMiddleClickPan(DataGrid grid)
    {
        Point  panStart    = default;
        double scrollStartH = 0, scrollStartV = 0;
        bool   panning     = false;
        ScrollBar? hBar    = null, vBar = null;
        bool   barsResolved = false;

        // Avalonia's DataGrid has no ScrollViewer in its template — it manages scrolling
        // itself via PART_HorizontalScrollbar and PART_VerticalScrollbar. Resolve them
        // lazily (visual tree isn't populated until after TemplateApplied).
        void ResolveScrollBars()
        {
            if (barsResolved) return;
            barsResolved = true;
            foreach (var d in grid.GetVisualDescendants())
            {
                if (d is not ScrollBar sb) continue;
                if      (sb.Name == "PART_HorizontalScrollbar") hBar = sb;
                else if (sb.Name == "PART_VerticalScrollbar")   vBar = sb;
                if (hBar != null && vBar != null) break;
            }
        }

        // Re-resolve scroll bars if the template is ever re-applied.
        grid.TemplateApplied += (_, _) => { barsResolved = false; hBar = null; vBar = null; };

        // RoutingStrategies.Direct|Bubble + handledEventsToo:true ensures the handler fires
        // even though DataGrid rows/cells mark PointerPressed handled (for row selection).
        grid.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (e.GetCurrentPoint(grid).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;

            ResolveScrollBars();

            panning      = true;
            panStart     = e.GetPosition(grid);
            scrollStartH = hBar?.Value ?? 0;
            scrollStartV = vBar?.Value ?? 0;
            e.Pointer.Capture(grid);
            grid.Cursor  = new Cursor(StandardCursorType.SizeAll);
            e.Handled    = true;
        }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);

        grid.AddHandler(InputElement.PointerMovedEvent, (object? _, PointerEventArgs e) =>
        {
            if (!panning) return;

            // Release pan if the middle button was lifted outside a PointerReleased event.
            if (!e.GetCurrentPoint(grid).Properties.IsMiddleButtonPressed)
            {
                panning = false;
                e.Pointer.Capture(null);
                grid.Cursor = null;
                return;
            }

            var delta = e.GetPosition(grid) - panStart;

            if (hBar is not null)
            {
                hBar.Value = Math.Clamp(scrollStartH - delta.X, hBar.Minimum, hBar.Maximum);
                // Raise Thumb.DragDeltaEvent on the scrollbar — a public routed event whose
                // ScrollBar class handler calls OnScroll → fires Scroll event → DataGrid
                // processes the new Value without any reflection on private members.
                hBar.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent });
            }
            if (vBar is not null)
            {
                vBar.Value = Math.Clamp(scrollStartV - delta.Y, vBar.Minimum, vBar.Maximum);
                vBar.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent });
            }

            e.Handled = true;
        }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);

        grid.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (!panning) return;
            panning = false;
            e.Pointer.Capture(null);
            grid.Cursor = null;
            e.Handled   = true;
        }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
