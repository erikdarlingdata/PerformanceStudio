using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Helpers;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    private static string GetTabLabel(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb.Text ?? "Tab";
        if (tab.Header is string s)
            return s;
        return "Tab";
    }

    /* The glyph refresher CreateTab subscribes onto a query session, keyed by the tab it was
       built for, so DetachTabToWindow can take exactly its own subscription off again. The
       session OUTLIVES its tab on the detach path — the tab is discarded, the session moves
       into the detached window still holding a handler that closes over the dead tab's close
       button — and redock re-subscribes through CreateTab, so every detach/redock cycle used
       to pin one more dead TabItem (visual tree and all) to the session for the session's
       lifetime. A ConditionalWeakTable rather than a Dictionary so the bookkeeping cannot
       become its own leak: a tab closed normally dies together with its session, and its
       entry evaporates with the key. */
    private readonly ConditionalWeakTable<TabItem, Action> _tabDirtyGlyphUnhooks = new();

    private TabItem CreateTab(string label, Control content)
    {
        var headerText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        var closeBtn = new Button
        {
            Content = CloseGlyph,
            MinWidth = 22,
            MinHeight = 22,
            Width = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Brushes.Transparent,
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = content };
        closeBtn.Tag = tab;
        closeBtn.Click += CloseTab_Click;

        /* #462: the modified marker. Only query sessions have a dirty state, and the button
           has to re-decide on pointer-over as well as on edits, because hovering is what turns
           the dot back into a close button. */
        if (content is QuerySessionControl querySession)
        {
            void RefreshCloseGlyph() =>
                closeBtn.Content = CloseButtonGlyph(querySession.IsDirty, closeBtn.IsPointerOver);

            /* Named and written down rather than subscribed inline: of everything wired up
               here, this is the one subscription that lands on an object that can outlive the
               tab, so it is the one detach has to be able to undo — see _tabDirtyGlyphUnhooks.
               The pointer handlers below live and die with the button itself. */
            EventHandler refreshOnDirtyChange = (_, _) => RefreshCloseGlyph();
            querySession.DirtyStateChanged += refreshOnDirtyChange;
            _tabDirtyGlyphUnhooks.Add(tab, () => querySession.DirtyStateChanged -= refreshOnDirtyChange);

            closeBtn.PointerEntered += (_, _) => RefreshCloseGlyph();
            closeBtn.PointerExited += (_, _) => RefreshCloseGlyph();

            // A session can arrive already modified — re-docking a detached window builds a
            // fresh tab around a session that has been edited since it left.
            RefreshCloseGlyph();

            /* #496: every top-level query session passes through here exactly when it gets
               its first tab, which makes this the one wiring point for scratch content
               persistence — same single-subscription argument as the #495 tab watcher.
               Idempotent, because redock passes the same living session through again. */
            HookScratchPersistence(querySession);
        }

        // Middle-click to close
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
            {
                _ = TryCloseTabAsync(tab);
                e.Handled = true;
            }
        };

        // Right-click context menu
        var copyPathItem = new MenuItem { Header = "Copy Path", Tag = tab };
        // Only visible when tab content has a file path
        void RefreshCopyPathVisibility() => copyPathItem.IsVisible = GetTabFilePath(tab) != null;
        RefreshCopyPathVisibility();

        var contextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Rename Tab", Tag = new object[] { header, headerText } },
                copyPathItem,
                new Separator(),
                new MenuItem { Header = "Detach to Window", Tag = tab },
                new Separator(),
                new MenuItem { Header = "Close", Tag = tab, InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        /* #472: whether there is a path to copy is not a fact about the tab's birth. A scratch
           query gains one the moment it is saved, and a tab can lose one. The menu is only
           consulted when it opens, so that is when the question gets asked — the call above is
           just the answer for a menu nobody has opened yet. */
        contextMenu.Opening += (_, _) => RefreshCopyPathVisibility();

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += TabContextMenu_Click;

        header.ContextMenu = contextMenu;

        return tab;
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
            _ = TryCloseTabAsync(tab);
    }

    private void TabContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        var headerText = item.Header?.ToString();

        switch (headerText)
        {
            case "Rename Tab":
                if (item.Tag is object[] parts)
                    StartRename((StackPanel)parts[0], (TextBlock)parts[1]);
                break;

            case "Copy Path":
                if (item.Tag is TabItem pathTab)
                {
                    var path = GetTabFilePath(pathTab);
                    if (path != null)
                        _ = ClipboardHelper.TrySetTextAsync(this, path);
                }
                break;

            case "Close":
                if (item.Tag is TabItem tab)
                    _ = TryCloseTabAsync(tab);
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                    _ = CloseOtherTabsAsync(keepTab);
                break;

            case "Close All Tabs":
                _ = CloseTabsAsync(MainTabControl.Items.Cast<TabItem>().ToList());
                break;

            case "Detach to Window":
                if (item.Tag is TabItem detachTab)
                    DetachTabToWindow(detachTab);
                break;
        }
    }

    /// <summary>
    /// Retitles a tab. The header is a StackPanel whose first child carries the text,
    /// which is also what StartRename edits.
    /// </summary>
    private static void SetTabLabel(TabItem tab, string label)
    {
        if (tab.Header is StackPanel header && header.Children.Count > 0 && header.Children[0] is TextBlock text)
            text.Text = label;
    }

    /// <summary>
    /// The file a tab was opened from, or null when nothing on disk is behind it
    /// (a pasted plan, a scratch query, a Query Store tab).
    ///
    /// <para>Every tab shape that can carry a path has to be recognised here, because this one
    /// method answers two questions: what <see cref="SaveOpenPlans"/> writes down for the next
    /// session, and whether <b>Copy Path</b> appears on the tab's context menu. A shape it does
    /// not know about loses both without saying anything.</para>
    /// </summary>
    private static string? GetTabFilePath(TabItem tab) => GetContentFilePath(tab.Content as Control);

    /// <summary>
    /// The same answer keyed on the content itself, because since #490 the question is also
    /// asked about content with no tab behind it: a detached window holds the control that WAS
    /// a tab's content, and what file backs it is a fact about the control, not the strip.
    /// </summary>
    private static string? GetContentFilePath(Control? content)
    {
        // Plans opened from file are wrapped in a DockPanel with the viewer as the last child
        if (content is DockPanel dp)
        {
            foreach (var child in dp.Children)
            {
                if (child is PlanViewerControl v)
                    return v.SourceFilePath;
            }
        }

        // Queries are the session control itself, with no wrapper around it
        if (content is QuerySessionControl session)
            return session.SourceFilePath;

        return null;
    }

    /// <summary>
    /// What the session-restore list records for a tab's content: the file path when there
    /// is one, else the <c>scratch:&lt;guid&gt;</c> entry for a scratch session whose
    /// content has actually been persisted (#496), else nothing. Layered ON TOP of
    /// <see cref="GetContentFilePath"/> rather than folded into it, because that method
    /// also answers Copy Path — and a scratch buffer id is precisely not a path anyone
    /// should be handed to paste somewhere.
    ///
    /// <para>The no-id case is deliberate, not a gap: an empty scratch tab has no buffer
    /// (ids are minted at first persist), and restoring a parade of blank "Query N" tabs
    /// would make persistence feel like clutter. A scratch tab earns its entry by having
    /// content on disk worth coming back for.</para>
    /// </summary>
    private static string? GetContentSessionEntry(Control? content)
    {
        var path = GetContentFilePath(content);
        if (path != null)
            return path;

        if (content is QuerySessionControl { SourceFilePath: null, ScratchBufferId: { } id })
            return ScratchBufferStore.EntryFor(id);

        return null;
    }

    private void StartRename(StackPanel header, TextBlock headerText)
    {
        var textBox = new TextBox
        {
            Text = headerText.Text,
            FontSize = 12,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Hide the text, show the textbox
        headerText.IsVisible = false;
        header.Children.Insert(0, textBox);
        textBox.Focus();
        textBox.SelectAll();

        void CommitRename()
        {
            var newName = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName))
                headerText.Text = newName;

            headerText.IsVisible = true;
            header.Children.Remove(textBox);
        }

        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter || ke.Key == Key.Escape)
            {
                if (ke.Key == Key.Escape)
                    textBox.Text = headerText.Text; // revert
                CommitRename();
                ke.Handled = true;
            }
        };

        textBox.LostFocus += (_, _) => CommitRename();
    }

    /// <summary>
    /// Gets query text from a PlanViewerControl — uses QueryText if set,
    /// otherwise concatenates StatementText from all parsed statements.
    /// </summary>
    private static string GetQueryTextFromPlan(PlanViewerControl viewer)
    {
        if (!string.IsNullOrEmpty(viewer.QueryText))
            return viewer.QueryText;

        if (viewer.CurrentPlan == null)
            return "";

        var statements = viewer.CurrentPlan.Batches
            .SelectMany(b => b.Statements)
            .Select(s => s.StatementText)
            .Where(t => !string.IsNullOrEmpty(t));

        return string.Join(Environment.NewLine, statements);
    }

    /// <summary>
    /// Detaches a tab's content into a standalone free-floating window.
    /// The window's Close button closes it permanently.
    /// A "Re-dock" button in the toolbar allows the user to explicitly return the content to a tab.
    ///
    /// <para>#473: permanently used to mean silently. A query session that leaves the tab strip
    /// takes its unsaved edit with it, out of reach of both #462 prompts, so the window gets a
    /// close guard and the session goes on the detached register until it comes back or the
    /// window closes.</para>
    /// </summary>
    /// <returns>The detached window, or null when the tab had no content to detach.</returns>
    internal Window? DetachTabToWindow(TabItem tab)
    {
        var content = tab.Content as Control;
        if (content == null) return null;

        var label = GetTabLabel(tab);

        /* The session is about to leave this tab behind. Take the glyph subscription off it
           first: the handler is the one reference that would keep the discarded TabItem alive
           from the still-living session (see _tabDirtyGlyphUnhooks), and redock subscribes
           afresh through CreateTab. */
        if (_tabDirtyGlyphUnhooks.TryGetValue(tab, out var unhookDirtyGlyph))
        {
            unhookDirtyGlyph();
            _tabDirtyGlyphUnhooks.Remove(tab);
        }

        // Remove the tab
        MainTabControl.Items.Remove(tab);
        tab.Content = null;
        UpdateEmptyOverlay();

        if (content is QueryStoreHistoryControl historyControl)
            historyControl.ShowCloseButton(false);

        var detachedWindow = DetachedWindowHelper.ShowDetached(
            content,
            title: label,
            icon: this.Icon,
            backgroundBrush: (Avalonia.Media.IBrush?)this.FindResource("BackgroundBrush"),
            onRedock: c =>
            {
                ForgetDetachedTabContent(c);

                if (!IsShuttingDown)
                {
                    var newTab = CreateTab(label, c);
                    MainTabControl.Items.Add(newTab);
                    MainTabControl.SelectedItem = newTab;
                    UpdateEmptyOverlay();
                }
            },
            onClosing: c =>
            {
                ForgetDetachedTabContent(c);

                /* #496: a detached scratch window ACTUALLY closing is the session leaving
                   the app by the user's hand — the detached twin of TryCloseTabAsync's
                   drop. This callback never runs on redock (the helper's redocked latch
                   returns first), so a redocked scratch keeps its buffer. Safe at shutdown's
                   force-close too: by then every DIRTY scratch was resolved at the
                   #462/#469/#477 walk (saved sessions are no longer scratch, Don't-Saved
                   ones already dropped), so the only session this can still touch is a
                   clean one — empty, by scratch's definition of clean — whose buffer is at
                   most a stale leftover the flush rules would delete anyway. */
                if (c is QuerySessionControl { SourceFilePath: null } scratchSession)
                    DropScratchBuffer(scratchSession);

                if (c is QueryStoreHistoryControl hc)
                    hc.CancelFetch();
            },
            closeGuard: DetachedQueryCloseGuard);

        RememberDetachedTabContent(detachedWindow, content);

        /* #447 made Compare a window-wide count, and this session just left the window: the
           count it is showing is about tabs it can no longer reach. Nothing else recomputes it
           here — the session's sub-tab watcher fires on sub-tab changes only, and detaching
           changes none — so the pre-detach state stuck until the next plan landed. Recompute
           now; with no MainWindow above it any more, the method's own fallback counts the
           session's plans, which inside a detached window is the only honest answer. Redock
           needs no twin call: adding the tab back fires MainWindow's collection watcher. */
        if (content is QuerySessionControl detachedSession)
            detachedSession.UpdateCompareButtonState();

        return detachedWindow;
    }
}
