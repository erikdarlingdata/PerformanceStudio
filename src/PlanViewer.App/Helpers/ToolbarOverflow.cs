using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Moves the tail of a fixed toolbar row into a chevron menu when the row is wider than the space
/// it has, and brings it back when the space returns.
///
/// <para><b>Why.</b> Both toolbars are one non-wrapping row inside a ScrollViewer whose rail is
/// deliberately suppressed (a rail under a 28px button row adds its own height to the strip, which
/// is the row shift that layout exists to kill). That combination hides content behind a hidden
/// affordance: at a maximized 1536-logical display the session row's last commands simply were not
/// there, and nothing on screen said they existed. This is the affordance.</para>
///
/// <para><b>What it deliberately does not do.</b> It never re-flows, re-orders, re-sizes or
/// re-labels a button that stays on the row. Icon-only-at-narrow-widths was considered and
/// rejected: a toolbar that changes shape as you resize it is the layout instability the fixed-slot
/// row was built to end, and the icons here are not yet learned enough to stand without their
/// labels. Trailing commands leave the row whole and come back whole, and the ones in front of them
/// do not move by a pixel.</para>
///
/// <para><b>Two owners, one IsVisible.</b> A candidate's <c>IsVisible</c> is written by two
/// parties: the app, which shows or hides a button because of what is loaded (PlanViewerControl's
/// Statements button exists only for a plan with a statement list), and this class, which hides one
/// because there is no room. Neither may clobber the other, so the two intents are tracked
/// separately — <c>OwnerVisible</c> and <c>Collapsed</c> — and the control's actual visibility is
/// derived from both, never written directly by either: visible only when the app wants the command
/// AND there is room for it. A button the app has withdrawn is therefore neither on the row nor in
/// the menu, and reserves width for neither. The app says which it means through
/// <see cref="SetAvailable"/>, for the reason documented there; a direct write to the button's
/// IsVisible is undone rather than obeyed.</para>
/// </summary>
public sealed class ToolbarOverflow
{
    /// <summary>
    /// One button that may leave the row, as its call site knows it.
    /// </summary>
    /// <param name="Button">The button itself. It stays in the visual tree either way — collapsing
    /// hides it, so its enabled state, handlers and content survive untouched.</param>
    /// <param name="Icon">The geometry the button wears, so the menu entry can wear the same one.
    /// Passed rather than dug out of the button's content: the content is a StackPanel built by
    /// <see cref="AppIcons.MakeContent"/> and reaching into it to find the PathIcon would break the
    /// first time that shape changed.</param>
    /// <param name="Label">The button's label, which becomes the menu entry's header.</param>
    /// <param name="GroupSeparator">The divider immediately in FRONT of this button, when this
    /// button is the first of its group. Collapsing happens from the end of the row backwards, so
    /// the first button of a group is the last of that group to leave — which makes its leading
    /// divider exactly the one with nothing left to divide.</param>
    public sealed record Item(
        Button Button,
        StreamGeometry Icon,
        string Label,
        Border? GroupSeparator = null);

    private sealed class Candidate(Item item)
    {
        internal Button Button { get; } = item.Button;
        internal StreamGeometry Icon { get; } = item.Icon;
        internal string Label { get; } = item.Label;
        internal Border? GroupSeparator { get; } = item.GroupSeparator;

        /// <summary>What the row loses when this button leaves it: the button, its margins, this
        /// panel's spacing, and the leading divider's share where this button owns one. Measured
        /// while the button is on the row and re-measured on every pass it spends there, so it
        /// cannot go stale; NaN until it has been laid out at least once, and a candidate that has
        /// never been measured is never collapsed (there would be no way to know whether collapsing
        /// it helped).</summary>
        internal double Width = double.NaN;

        /// <summary>This class's half of the button's visibility: there is no room for it.</summary>
        internal bool Collapsed;

        /// <summary>The app's half: it wants this button on the toolbar at all.</summary>
        internal bool OwnerVisible;

        /// <summary>The menu entry standing in for this button, built once and reused. Reused
        /// rather than rebuilt so that its enabled state has somewhere to live between collapses,
        /// and so a test can hold on to it.</summary>
        internal MenuItem? Proxy;
    }

    private readonly ScrollViewer _host;
    private readonly Panel _panel;
    private readonly Button _chevron;
    private readonly List<Candidate> _candidates;
    private readonly double _spacing;

    /// <summary>True while this class is writing a button's IsVisible, so the observer that records
    /// the app's intent does not mistake our own write for one.</summary>
    private bool _applying;

    /// <summary>Guards against re-entering a pass from a layout the pass itself provoked.</summary>
    private bool _refreshing;

    /// <summary>Set when the collapsed set changed while the menu was open. Rebuilding a live menu
    /// detaches its items and drops keyboard focus mid-navigation, so the rebuild waits for
    /// <see cref="MenuFlyout.Closed"/> instead.</summary>
    private bool _menuSyncDeferred;

    private ToolbarOverflow(ScrollViewer host, Panel panel, Button chevron, MenuFlyout menu, IReadOnlyList<Item> items)
    {
        _host = host;
        _panel = panel;
        _chevron = chevron;
        Menu = menu;
        _spacing = (panel as StackPanel)?.Spacing ?? 0;
        _candidates = items.Select(i => new Candidate(i) { OwnerVisible = i.Button.IsVisible }).ToList();
    }

    /// <summary>The menu the chevron opens. Its entries are, in order, the buttons that have left
    /// the row.</summary>
    public MenuFlyout Menu { get; }

    /// <summary>
    /// How many times the set of collapsed buttons has changed since this was attached.
    ///
    /// <para>Exposed because the one failure mode this design can have is a layout loop — hiding a
    /// button re-measures the row, which is what asks this class whether to hide a button — and the
    /// only convincing proof that it settles is that a stable window stops producing revisions. The
    /// test suite pins exactly that.</para>
    /// </summary>
    public int Revisions { get; private set; }

    /// <summary>
    /// Wires a toolbar's overflow up and returns the live instance.
    /// </summary>
    /// <param name="host">The ScrollViewer holding the row. Its width is the space the row has, and
    /// its layout passes are what drive re-evaluation.</param>
    /// <param name="chevron">The button that opens the menu. It must live OUTSIDE
    /// <paramref name="host"/> — docked beside it — or the affordance for the commands that
    /// scrolled away would scroll away too. Shown only while the menu holds something.</param>
    /// <param name="menu">The flyout to fill. Assigned to the chevron here.</param>
    /// <param name="items">The buttons that may leave the row, in the order they leave it: the
    /// first entry is the first to go into the menu, so this list runs from the END of the row
    /// backwards. Everything not in it stays put at every width.</param>
    public static ToolbarOverflow Attach(
        ScrollViewer host,
        Button chevron,
        MenuFlyout menu,
        IReadOnlyList<Item> items)
    {
        if (host.Content is not Panel panel)
            throw new ArgumentException("The overflow host must hold the toolbar row directly.", nameof(host));

        var overflow = new ToolbarOverflow(host, panel, chevron, menu, items);
        chevron.Flyout = menu;
        menu.Closed += (_, _) =>
        {
            if (!overflow._menuSyncDeferred)
                return;
            overflow._menuSyncDeferred = false;
            overflow.SyncMenu();
        };

        foreach (var candidate in overflow._candidates)
            candidate.Button.PropertyChanged += overflow.OnCandidatePropertyChanged;

        /* LayoutUpdated rather than a Bounds subscription: the row's natural width is read from the
           panel's DesiredSize, and DesiredSize is only trustworthy once a layout pass has finished.
           This also covers every reason the answer could change at once -- the window resized, a
           button appeared, the chevron itself took a bite out of the row's space -- without this
           class having to enumerate them. A pass that changes nothing does no work beyond a little
           arithmetic over at most a handful of candidates, which is the normal case. */
        host.LayoutUpdated += (_, _) => overflow.Refresh();

        return overflow;
    }

    /// <summary>
    /// Re-decides what fits. Called after every layout pass; safe to call directly.
    /// </summary>
    public void Refresh()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            RefreshCore();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshCore()
    {
        var viewport = _host.Viewport.Width > 0 ? _host.Viewport.Width : _host.Bounds.Width;
        if (viewport <= 0)
            return; // not laid out yet — nothing to decide against

        /* A button the app has hidden is not "collapsed", it is simply absent: it costs the row
           nothing and belongs in neither place. Normalizing here keeps the collapsed set a prefix
           of the candidates the app is currently offering, which is what makes restoring them in
           reverse order a matter of walking the list backwards. */
        foreach (var candidate in _candidates)
        {
            if (!candidate.OwnerVisible)
                candidate.Collapsed = false;
            else if (!candidate.Collapsed)
                Measure(candidate);
        }

        /* What the row asks for AS IT STANDS, with whatever is already in the menu left out of it.
           The ScrollViewer measures its content against infinite width, so this is the row's
           natural width and not a clipped one. Read fresh every pass rather than carried between
           them: LayoutUpdated runs after the layout that applied the last pass's decisions, so this
           number already reflects them, and a cached width that drifted a pixel corrects itself
           here instead of compounding. */
        var required = _panel.DesiredSize.Width;
        var before = _candidates.Count(c => c.Collapsed);

        // Too wide: take from the end of the row, in the order the caller gave.
        foreach (var candidate in _candidates)
        {
            if (required <= viewport)
                break;
            if (candidate.Collapsed || !candidate.OwnerVisible || double.IsNaN(candidate.Width))
                continue;

            candidate.Collapsed = true;
            ApplyVisibility(candidate);
            required -= candidate.Width;
        }

        /* Room again: give it back in the reverse order it was taken, so a button always returns to
           the slot it left. Walking the list backwards yields exactly that, and stopping at the
           first one that will not fit keeps the collapsed set contiguous -- no gap where a button
           sits in the menu while a later one is back on the row. */
        for (var i = _candidates.Count - 1; i >= 0; i--)
        {
            var candidate = _candidates[i];
            if (!candidate.Collapsed)
                continue;
            if (required + candidate.Width > viewport)
                break;

            candidate.Collapsed = false;
            ApplyVisibility(candidate);
            required += candidate.Width;
        }

        /* The collapsed set is always a contiguous prefix of the offered candidates (the
           normalization above plus take-from-the-front / restore-from-the-back keep it one), so an
           unchanged count means an unchanged set. LayoutUpdated delivers EVERY pass in the window
           - each editor keystroke, each canvas redraw - and this is where the overwhelmingly
           common nothing-changed pass gets out before allocating anything in SyncMenu. */
        if (_candidates.Count(c => c.Collapsed) == before)
            return;

        Revisions++;
        SyncMenu();
    }

    /// <summary>
    /// Re-reads what this button costs the row, while it is on the row to be read.
    /// </summary>
    private void Measure(Candidate candidate)
    {
        if (candidate.Button.Bounds.Width <= 0)
            return;

        var margin = candidate.Button.Margin;
        var width = candidate.Button.Bounds.Width + margin.Left + margin.Right + _spacing;

        /* The divider in front of the group goes with the last member of that group, so its gutters
           are part of what leaving costs. Counted only while it is actually on the row: the plan
           toolbar's Statements divider is hidden along with its button whenever no plan has
           statements to list. */
        if (candidate.GroupSeparator is { IsVisible: true } separator && separator.Bounds.Width > 0)
        {
            var separatorMargin = separator.Margin;
            width += separator.Bounds.Width + separatorMargin.Left + separatorMargin.Right + _spacing;
        }

        candidate.Width = width;
    }

    /// <summary>
    /// Writes the one visibility both owners share. The app's intent and this class's are ANDed:
    /// on the row only when the app wants the button there and there is room for it.
    /// </summary>
    private void ApplyVisibility(Candidate candidate)
    {
        var visible = candidate.OwnerVisible && !candidate.Collapsed;

        _applying = true;
        try
        {
            candidate.Button.IsVisible = visible;
            if (candidate.GroupSeparator is { } separator)
                separator.IsVisible = visible;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// The app's half of a collapsible button's visibility: whether this command applies to what is
    /// loaded at all. Call sites use this INSTEAD of setting the button's <c>IsVisible</c>, and the
    /// button's leading group divider moves with it.
    ///
    /// <para><b>Why there is an explicit call rather than an observer.</b> The obvious design is to
    /// watch the button's IsVisible and read the app's intent off it, so no call site has to know
    /// this class exists. It does not work, and fails in the one direction that matters: once this
    /// class has collapsed a button, the button is already hidden, so the app setting IsVisible to
    /// false is a no-op that raises no change notification at all. The command would then sit in
    /// the chevron menu for a plan it no longer applies to — offering "Statements" for a plan with
    /// no statements — which is a worse bug than the one the menu was added to fix. Intent that
    /// cannot be inferred has to be told.</para>
    /// </summary>
    /// <param name="button">One of the buttons handed to <see cref="Attach"/>. Anything else is
    /// ignored, so a caller cannot half-register a control by calling this for it.</param>
    /// <param name="available">Whether the command applies at all. A button that does not is on
    /// neither the row nor the menu, and reserves width for neither.</param>
    public void SetAvailable(Button button, bool available)
    {
        var candidate = _candidates.FirstOrDefault(c => ReferenceEquals(c.Button, button));
        if (candidate is null || candidate.OwnerVisible == available)
            return;

        candidate.OwnerVisible = available;

        /* Applied here rather than left to the next layout pass, so a button the app has just
           withdrawn is gone at once. One it has just offered reappears at once too - possibly for
           a single clipped frame on a row with no room, since its Collapsed flag was normalized
           away while it was unavailable; the pass that follows collapses it properly if the room
           is not there. */
        ApplyVisibility(candidate);
        SyncMenu();
    }

    private void OnCandidatePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        var candidate = _candidates.FirstOrDefault(c => ReferenceEquals(c.Button, sender));
        if (candidate is null)
            return;

        if (e.Property == Visual.IsVisibleProperty)
        {
            /* Enforcement, not intent — intent arrives through SetAvailable. A direct write to a
               collapsible button's IsVisible from anywhere else is a bug, and this makes it a loud
               one: the write is undone on the spot, so the button visibly refuses to move rather
               than quietly stranding itself on a row with no room for it or in a menu for a command
               that no longer applies. */
            if (!_applying)
                ApplyVisibility(candidate);
        }
        else if (e.Property == InputElement.IsEnabledProperty && candidate.Proxy is { } proxy)
        {
            // The menu entry is the same command by another door; it cannot be offered while the
            // button it stands for is refusing, in either direction.
            SyncProxyEnabled(proxy, candidate.Button.IsEnabled);
        }
        else if (e.Property == ToolTip.TipProperty && candidate.Proxy is { } tipProxy)
        {
            // Tooltips move too: ComparePlansButtonState swaps the button's tip with its enabled
            // state, and a menu entry frozen on the old one would explain the wrong door.
            ToolTip.SetTip(tipProxy, ToolTip.GetTip(candidate.Button));
        }
    }

    /// <summary>
    /// Mirrors enabled-state onto a proxy AND its icon's opacity. Fluent's disabled MenuItem dims
    /// only the header presenter; the icon keeps the full-brightness foreground it inherits, so a
    /// disabled entry would read as a lit icon beside greyed text. 0.4 matches AppButton's own
    /// disabled opacity, so the command dims the same way through either door.
    /// </summary>
    private static void SyncProxyEnabled(MenuItem proxy, bool enabled)
    {
        proxy.IsEnabled = enabled;
        if (proxy.Icon is Control icon)
            icon.Opacity = enabled ? 1.0 : 0.4;
    }

    /// <summary>
    /// Brings the menu into line with the collapsed set, and the chevron into line with the menu.
    /// Rebuilt from the candidate order rather than pushed at on each change, so the entries cannot
    /// drift out of order and a button the app has hidden cannot be left behind in the menu.
    /// </summary>
    private void SyncMenu()
    {
        var wanted = _candidates
            .Where(c => c.Collapsed && c.OwnerVisible)
            .Select(ProxyFor)
            .ToList();

        /* An emptied menu closes rather than lingers: hiding the chevron does not close an open
           flyout, so a resize that restores every command (Win+Up, snap, DPI change) would
           otherwise leave an empty popup floating over the row, anchored to a button that is no
           longer there. Hide() raises Closed synchronously, which lets the rebuild below proceed
           in the same pass. */
        if (wanted.Count == 0 && Menu.IsOpen)
            Menu.Hide();

        if (!Menu.Items.SequenceEqual(wanted))
        {
            if (Menu.IsOpen)
            {
                // Clear() on a live menu detaches its items and drops keyboard focus out from
                // under whoever is arrowing through it; the rebuild waits for Closed.
                _menuSyncDeferred = true;
            }
            else
            {
                Menu.Items.Clear();
                foreach (var item in wanted)
                    Menu.Items.Add(item);
            }
        }

        _chevron.IsVisible = wanted.Count > 0;
    }

    private MenuItem ProxyFor(Candidate candidate)
    {
        if (candidate.Proxy is { } existing)
            return existing;

        var proxy = new MenuItem
        {
            Header = candidate.Label,
            Icon = AppIcons.MakeIcon(candidate.Icon)
        };

        SyncProxyEnabled(proxy, candidate.Button.IsEnabled);
        ToolTip.SetTip(proxy, ToolTip.GetTip(candidate.Button));

        /* Raising the button's own Click is what keeps the two doors onto a command from ever
           drifting apart: there is one handler, and the menu does not get a copy of it, a command
           object, or a second opinion about when it may run. */
        proxy.Click += (_, _) => candidate.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        candidate.Proxy = proxy;
        return proxy;
    }
}
