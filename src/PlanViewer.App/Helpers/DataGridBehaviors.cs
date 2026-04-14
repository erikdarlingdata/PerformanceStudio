using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Attaches middle-mouse-button pan behavior to a DataGrid.
/// </summary>
public static class DataGridBehaviors
{
    // Cached reflection accessors for DataGrid internal scroll methods.
    // ProcessHorizontalScroll / ProcessVerticalScroll read the scrollbar's current Value
    // and reposition the grid content — the same code path used during real scrollbar interaction.
    private static readonly MethodInfo? _processHScroll =
        typeof(DataGrid).GetMethod("ProcessHorizontalScroll", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? _processVScroll =
        typeof(DataGrid).GetMethod("ProcessVerticalScroll",   BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Attach middle-click pan behavior to <paramref name="grid"/>.</summary>
    public static void Attach(DataGrid grid)
    {
        AttachMiddleClickPan(grid);
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
                hBar.Value = Math.Clamp(scrollStartH + delta.X, hBar.Minimum, hBar.Maximum);
                _processHScroll?.Invoke(grid, [ScrollEventType.ThumbTrack]);
            }
            if (vBar is not null)
            {
                vBar.Value = Math.Clamp(scrollStartV + delta.Y, vBar.Minimum, vBar.Maximum);
                _processVScroll?.Invoke(grid, [ScrollEventType.ThumbTrack]);
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
