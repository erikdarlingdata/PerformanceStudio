using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Helpers;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;

    /// <summary>
    /// Full path on disk when the query was loaded from, or last saved to, a file.
    /// </summary>
    public string? SourceFilePath { get; set; }

    /// <summary>
    /// Identity of this session's persisted scratch buffer (#496), or null while it has
    /// none. Assigned by MainWindow the first time a never-saved session's content is
    /// actually written to the scratch store — not at construction, so an empty tab never
    /// mints a buffer — and carried back onto the restored session at the next start, which
    /// is what makes a restored scratch CONTINUE its buffer instead of forking a new one.
    /// Cleared when the buffer is deleted: the user chose its fate at a prompt (Don't Save,
    /// or a save that moved the content into a real file), or there is nothing unsaved left
    /// to protect.
    ///
    /// <para>On the session rather than the tab for the same reason <see cref="SourceFilePath"/>
    /// is: detach discards the TabItem and the session lives on in its own window (#473),
    /// and its buffer identity has to travel with it.</para>
    /// </summary>
    internal Guid? ScratchBufferId { get; set; }

    /// <summary>
    /// The encoding the file behind <see cref="SourceFilePath"/> declared with its byte order
    /// mark, or null for a BOM-less file and for a scratch session — both of which save as
    /// UTF-8 without a BOM, which is what every save wrote before this existed.
    ///
    /// <para>Captured at open so a save writes the bytes the file arrived with. SSMS writes
    /// .sql files as UTF-16 with a BOM; opening one read fine (File.ReadAllText honors the
    /// mark) and then the first Ctrl+S silently transcoded the whole file to UTF-8 — every
    /// byte changed, the BOM gone, without the user asking for any of it.</para>
    /// </summary>
    public Encoding? SourceFileEncoding { get; set; }

    /// <summary>
    /// The editor text as of the last load or save. A new session starts empty, so a
    /// never-saved scratch tab with anything typed into it is dirty too (#462).
    /// </summary>
    private string _savedText = "";

    /// <summary>
    /// Whether the editor holds work that is not on disk.
    ///
    /// <para>This compares text rather than latching a "was edited" bool on the first
    /// keystroke, so typing something and typing it back out again leaves the session
    /// clean — an undo to the original is not unsaved work, and prompting about it is
    /// how a save prompt teaches people to dismiss save prompts.</para>
    /// </summary>
    public bool IsDirty => !string.Equals(QueryEditor.Text, _savedText, StringComparison.Ordinal);

    /// <summary>
    /// Raised whenever the editor text changes or the session is marked clean. The tab
    /// header subscribes to this to keep its modified marker honest; the session cannot
    /// reach its own tab, and polling <see cref="IsDirty"/> per render would be worse.
    /// </summary>
    public event EventHandler? DirtyStateChanged;

    /// <summary>
    /// Declares the current text to be what is on disk. Called after a load and after a
    /// successful save — not after a failed one, which must leave the session dirty.
    /// </summary>
    public void MarkClean()
    {
        _savedText = QueryEditor.Text;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private ServerConnection? _serverConnection;
    private string? _connectionString;
    private string? _selectedDatabase;
    private int _planCounter;
    private CancellationTokenSource? _executionCts;
    private ServerMetadata? _serverMetadata;

    // TextMate installation for syntax highlighting
    private TextMate.Installation? _textMateInstallation;
    private CancellationTokenSource? _statusClearCts;
    private CompletionWindow? _completionWindow;

    public QuerySessionControl(ICredentialService credentialService, ConnectionStore connectionStore)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        InitializeComponent();

        // Initialize editor with empty text so the document is ready
        QueryEditor.Text = "";
        ZoomBox.SelectedIndex = 2; // 100%

        SetupSyntaxHighlighting();
        SetupEditorContextMenu();

        // Keybindings: F5/Ctrl+E for Execute, Ctrl+L for Estimated Plan
        KeyDown += OnKeyDown;

        // Ctrl+mousewheel for font zoom — use Tunnel so it fires before ScrollViewer consumes scroll-down
        QueryEditor.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnEditorPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        /* The toolbar is one non-wrapping row that scrolls when it is wider than the window, and
           Avalonia only turns a vertical wheel into horizontal scrolling when Shift is held
           (ScrollContentPresenter.OnPointerWheelChanged swaps the delta vector on Shift alone).
           A toolbar you can only pan with a modifier held is a toolbar nobody pans, so a plain
           wheel over it scrolls it. Tunnel, so the buttons underneath never eat the wheel first.

           One deliberate consequence: while the toolbar overflows, this also swallows the wheel
           over the database ComboBox, which would otherwise change the database under you. An
           accidental scroll that silently moves your execution context is the worse of the two. */
        ToolbarScroll.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnToolbarWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Code completion
        QueryEditor.TextArea.TextEntering += OnTextEntering;
        QueryEditor.TextArea.TextEntered += OnTextEntered;

        // #462: every edit is a chance for the tab's modified marker to change, in both
        // directions — an undo back to the saved text clears it again.
        QueryEditor.TextChanged += (_, _) =>
        {
            DirtyStateChanged?.Invoke(this, EventArgs.Empty);
            // The first keystroke is what takes the empty state away, and deleting the last
            // one is what brings it back.
            RefreshEmptyState();
        };

        // Focus the editor when the control is attached to the visual tree
        // Re-install TextMate if it was disposed on detach (tab switching disposes it)
        AttachedToVisualTree += (_, _) =>
        {
            if (_textMateInstallation == null)
                SetupSyntaxHighlighting();

            FocusEditor();

            /* The empty state's recent plans come off the owning window, which a session built
               moments ago cannot see yet. Attaching is when it can. */
            RefreshEmptyState();
        };

        // Dispose TextMate when detached (e.g. tab switch) to release renderers/transformers.
        /* Emptying the strip cancels the in-flight status-clear dispatch — it must not fire on a
           dead control — and, since the timer is what would have taken the message down, it is
           also the only thing that can: a session detaches when the user switches to another
           top-level tab, and whatever the strip was saying would otherwise be waiting, timer
           cancelled and therefore forever, when they came back. */
        DetachedFromVisualTree += (_, _) =>
        {
            _textMateInstallation?.Dispose();
            _textMateInstallation = null;
            ClearStatus();
        };

        /* #447: a plan appearing in — or leaving — this session changes whether Compare Plans is
           offered in every session in the window, not just this one. Watched here rather than
           called at each site that produces a plan, because the sites that produce a plan are the
           ones nobody remembers: executing a query fills in a tab that already exists, which is
           neither an Add nor a Remove and is exactly the case the first fix missed. */
        TabContentWatcher.Watch(SubTabControl, () =>
        {
            UpdateCompareButtonState();
            // A plan, Query Store grid or schema tab opening or closing decides the other half
            // of whether this session is empty.
            RefreshEmptyState();
        });

        // Focus the editor when the Editor tab is selected; toggle plan-dependent buttons
        SubTabControl.SelectionChanged += (_, _) =>
        {
            if (SubTabControl.SelectedIndex == 0)
                FocusEditor();
            UpdatePlanTabButtonState();

            /* The strip sits above the sub-tabs and says nothing about which one it is talking
               about, so a message that outlives its view reads as a complaint about the view the
               user moved to. Whatever it was saying was about the view they just left. */
            ClearStatus();
        };

        /* A brand new session is the empty state's whole reason for existing, and neither the
           watcher (which only reports changes) nor a keystroke (there has been none) would say
           so. The attach above refreshes it again once there is a window to read recent plans
           from. */
        RefreshEmptyState();
    }


    // Schema context menu items — stored as fields so we can toggle visibility on menu open
    private MenuItem? _showIndexesItem;
    private MenuItem? _showTableDefItem;
    private MenuItem? _showObjectDefItem;
    private Separator? _schemaSeparator;
    private ResolvedSqlObject? _contextMenuObject;


    private enum SchemaInfoKind { Indexes, TableDefinition, ObjectDefinition }


    private (string prefix, int startOffset) GetWordBeforeCaret()
    {
        var doc = QueryEditor.Document;
        var offset = QueryEditor.CaretOffset;
        var start = offset;

        while (start > 0)
        {
            var ch = doc.GetCharAt(start - 1);
            if (char.IsLetterOrDigit(ch) || ch == '_')
                start--;
            else
                break;
        }

        return (doc.GetText(start, offset - start), start);
    }


    private bool IsAzureConnection =>
        _serverConnection != null &&
        (_serverConnection.ServerName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
         _serverConnection.ServerName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase));


    private (AnalysisResult? Analysis, PlanViewerControl? Viewer) GetCurrentAnalysisWithViewer()
    {
        // Find the currently selected plan tab's PlanViewerControl
        if (SubTabControl.SelectedItem is TabItem tab && tab.Content is PlanViewerControl viewer
            && viewer.CurrentPlan != null)
        {
            return (ResultMapper.Map(viewer.CurrentPlan, "query editor", _serverMetadata, viewer.QueryText), viewer);
        }

        // Fallback: find the most recent plan tab
        for (int i = SubTabControl.Items.Count - 1; i >= 0; i--)
        {
            if (SubTabControl.Items[i] is TabItem planTab && planTab.Content is PlanViewerControl v
                && v.CurrentPlan != null)
            {
                /* Same session, same server: the fallback tab's advice gets the Server Context
                   section the selected-tab path above already had. */
                return (ResultMapper.Map(v.CurrentPlan, "query editor", _serverMetadata, v.QueryText), v);
            }
        }

        return (null, null);
    }


    /// <summary>
    /// Pans the fixed toolbar row when it is wider than the window. Wheel up scrolls left, the
    /// same direction the tab strip's handler moves, and a horizontal wheel (trackpad swipe)
    /// wins over the vertical one when the device sends both.
    /// </summary>
    private void OnToolbarWheel(object? sender, PointerWheelEventArgs e)
    {
        var max = Math.Max(0, ToolbarScroll.Extent.Width - ToolbarScroll.Viewport.Width);
        if (max <= 0)
            return; // whole toolbar is visible — leave the wheel to whatever is under it

        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (delta == 0)
            return;

        ToolbarScroll.Offset = new Avalonia.Vector(
            Math.Clamp(ToolbarScroll.Offset.X - (delta * 48), 0, max),
            ToolbarScroll.Offset.Y);
        e.Handled = true;
    }

    /* The colours the theme holds today, as literals, so a key missing from the dictionary renders
       something sane rather than nothing. Three of them are exactly what the code used to construct
       inline at each site; FallbackMuted is not, because the muted site was constructing #A0A0A0
       and the theme's muted brush is #B0B6C0 -- taking the token means taking its colour. */
    private static readonly IBrush FallbackForeground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB));
    private static readonly IBrush FallbackBackground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23));
    private static readonly IBrush FallbackMuted = new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xC0));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));

    /// <summary>
    /// A theme brush by key, or <paramref name="fallback"/> when the key is not in the
    /// dictionary — so a control still renders something sane if a token is missing.
    /// </summary>
    private static IBrush Token(IResourceHost host, string key, IBrush fallback) =>
        host.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    private IBrush Token(string key, IBrush fallback) => Token(this, key, fallback);

    private IBrush ForegroundToken => Token("ForegroundBrush", FallbackForeground);
    private IBrush BackgroundToken => Token("BackgroundBrush", FallbackBackground);
    private IBrush MutedToken => Token("ForegroundMutedBrush", FallbackMuted);


    public IEnumerable<(string label, PlanViewerControl viewer)> GetPlanTabs()
    {
        foreach (var item in SubTabControl.Items)
        {
            if (item is TabItem tab && tab.Content is PlanViewerControl viewer
                && viewer.CurrentPlan != null)
            {
                yield return (GetTabLabel(tab), viewer);
            }
        }
    }


}
