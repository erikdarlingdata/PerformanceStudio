using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace PlanViewer.App.Controls;

/* The session's document strip — the plan, Query Store and schema tabs the session opens, said
   once — and the surface machine that decides whether the user is looking at one of them at all.

   A session shows exactly one surface: the Editor view, the Overview view, or a document. The rule
   that keeps the rest of the session simple is DESELECT-ON-LEAVE: a view never coexists with a
   selected document, so leaving the document surface deselects the strip. That one rule is what
   empties the host, detaches the document that was showing (which is what lets a History fetch
   cancel itself), leaves SelectedDocument naturally null on a view with nothing to gate it, and
   turns coming back to a document into an ordinary selection change from null rather than a
   re-click on something already selected.

   THIS FILE IS THE ONLY PLACE ALLOWED TO REACH INTO THE STRIP'S ITEMS OR ITS SELECTION, and the
   only place allowed to write the surface. Every other partial adds, removes, selects and
   enumerates through the members below. Wiring an event to the control is the one exemption, for
   the reason that leaves nothing to insulate: it keeps its name and its type. */
public partial class QuerySessionControl : UserControl
{
    /// <summary>
    /// What a session can be showing. The two views are not documents and never will be: the
    /// editor is permanent, and the Overview is a live look at the server rather than something
    /// the session opened and can close.
    /// </summary>
    internal enum SessionSurface { Editor, Overview, Documents }

    private SessionSurface _surface = SessionSurface.Editor;

    /// <summary>
    /// Which surface the session is showing. Internal so a test can ask what the user ended up
    /// looking at — a question the strip used to answer with an index and no longer can, because
    /// neither view is in it.
    /// </summary>
    internal SessionSurface SelectedView => _surface;

    /// <summary>
    /// Every document this session holds, in strip order.
    /// </summary>
    private IEnumerable<TabItem> DocumentTabs => SubTabControl.Items.OfType<TabItem>();

    /// <summary>
    /// Whether the session holds any document at all.
    /// </summary>
    private bool HasDocuments => SubTabControl.Items.Count > 0;

    /// <summary>
    /// The document the user is looking at, or null when that is one of the views. The strip is
    /// genuinely deselected on a view, so this needs no surface test of its own.
    /// </summary>
    private TabItem? SelectedDocument => SubTabControl.SelectedItem as TabItem;

    /// <summary>
    /// Whether the editor, rather than the Overview or a document, is what the session is showing.
    /// </summary>
    private bool IsEditorSelected => _surface == SessionSurface.Editor;

    /// <summary>
    /// Lets the strip show no selection at all. Called once, from the constructor.
    /// </summary>
    /// <remarks>
    /// <para>The default <c>SelectionMode</c> is <c>AlwaysSelected</c>, which silently coerces
    /// <c>SelectedItem = null</c> straight back to the tab that was selected: the deselect returns
    /// without complaining and the strip stays lit underneath the view. <c>Single</c> is the mode
    /// that lets a selection genuinely be nothing, and it changes nothing else — index and
    /// keyboard selection behave exactly as they did.</para>
    ///
    /// <para>Through the registry because there is no other door from here: the property field is
    /// protected and the CLR accessors are non-public, so it can be set neither as a XAML attribute
    /// nor by a Setter in a theme. This line is load-bearing and it is exactly the kind of
    /// unexplained default a later reader tidies away — without it the whole surface machine
    /// degrades to "a view with a document still selected behind it", quietly.</para>
    /// </remarks>
    private void MakeStripDeselectable()
    {
        var selectionMode = AvaloniaPropertyRegistry.Instance
            .FindRegistered(SubTabControl, "SelectionMode")!;
        SubTabControl.SetValue(selectionMode, SelectionMode.Single);
    }

    /// <summary>Puts a new document at the end of the strip. It does not become the selected one.</summary>
    /// <remarks>
    /// Deliberately: a deselectable strip does not select what is added to it while nothing is
    /// selected, so every caller pairs this with <see cref="SelectDocument"/>. The pairing is what
    /// puts the user on the document they asked for, not a leftover from when it was implicit.
    /// </remarks>
    private void AddDocument(TabItem tab) => SubTabControl.Items.Add(tab);

    /// <summary>
    /// Takes a document out of the strip, and lands the user somewhere real if it was the one they
    /// were looking at.
    /// </summary>
    /// <remarks>
    /// <para>Removing the selected document clears the selection outright, even when other
    /// documents remain — it does not move along to a neighbour. So there are two rules, not one,
    /// and both run after <c>Items.Remove</c> has returned, which is where the selection change
    /// has already completed.</para>
    ///
    /// <para>Both are gated on the user actually being ON the document surface. Closing a document
    /// from a view — the tab strip is visible from every surface, and its close buttons work from
    /// all of them — must leave the user exactly where they were.</para>
    /// </remarks>
    private void RemoveDocument(TabItem tab)
    {
        var index = SubTabControl.Items.IndexOf(tab);
        SubTabControl.Items.Remove(tab);

        if (_surface != SessionSurface.Documents)
            return;

        if (!HasDocuments)
        {
            SelectEditor();
            return;
        }

        if (SubTabControl.SelectedItem == null)
        {
            // Whatever took its place in the strip, or the last document if it was the last.
            var neighbour = index >= 0 && index < SubTabControl.Items.Count
                ? index
                : SubTabControl.Items.Count - 1;
            if (SubTabControl.Items[neighbour] is TabItem next)
                SelectDocument(next);
        }
    }

    /// <summary>Brings a document to the front.</summary>
    private void SelectDocument(TabItem tab)
    {
        SetSurface(SessionSurface.Documents);
        SubTabControl.SelectedItem = tab;
    }

    /// <summary>Goes to the editor view, and deselects the strip behind it.</summary>
    private void SelectEditor()
    {
        SetSurface(SessionSurface.Editor);
        SubTabControl.SelectedItem = null;
    }

    /// <summary>Goes to the Overview view, and deselects the strip behind it.</summary>
    private void SelectOverview()
    {
        SetSurface(SessionSurface.Overview);
        SubTabControl.SelectedItem = null;
    }

    /// <summary>
    /// The one place the surface changes: shows the new one and empties the status strip, but only
    /// when the surface actually moved.
    /// </summary>
    /// <remarks>
    /// <para>Only on an actual move, because several callers set a status one line after asking for
    /// a surface, and a clear that fired unconditionally would wipe the message they just wrote.
    /// For the same reason the flip must never be hung off a tab's content changing: filling in a
    /// document that is already showing is not a change of surface, and treating it as one erases
    /// the message the caller set immediately before filling it.</para>
    ///
    /// <para>Synchronously, because those callers depend on the clear having already happened by
    /// the time they write. A dispatcher-posted flip arrives after them and takes the message with
    /// it.</para>
    /// </remarks>
    private void SetSurface(SessionSurface surface)
    {
        if (_surface == surface)
            return;

        _surface = surface;
        ApplySurface();

        /* The strip sits above the surfaces and says nothing about which one it is talking about,
           so a message that outlives its surface reads as a complaint about the one the user moved
           to. Whatever it was saying was about the one they just left. */
        ClearStatus();

        /* Arriving at the editor is what puts the caret back in it. This used to hang off the tab
           strip's selection, which is no longer where the editor lives. */
        if (surface == SessionSurface.Editor)
            FocusEditor();
    }
}
