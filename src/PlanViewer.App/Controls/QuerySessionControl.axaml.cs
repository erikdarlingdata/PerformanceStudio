using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        // Code completion
        QueryEditor.TextArea.TextEntering += OnTextEntering;
        QueryEditor.TextArea.TextEntered += OnTextEntered;

        // #462: every edit is a chance for the tab's modified marker to change, in both
        // directions — an undo back to the saved text clears it again.
        QueryEditor.TextChanged += (_, _) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);

        // Focus the editor when the control is attached to the visual tree
        // Re-install TextMate if it was disposed on detach (tab switching disposes it)
        AttachedToVisualTree += (_, _) =>
        {
            if (_textMateInstallation == null)
                SetupSyntaxHighlighting();

            QueryEditor.Focus();
            QueryEditor.TextArea.Focus();
        };

        // Dispose TextMate when detached (e.g. tab switch) to release renderers/transformers.
        // Also cancel any in-flight status-clear dispatch so it doesn't fire on a dead control.
        DetachedFromVisualTree += (_, _) =>
        {
            _textMateInstallation?.Dispose();
            _textMateInstallation = null;
            _statusClearCts?.Cancel();
            _statusClearCts?.Dispose();
            _statusClearCts = null;
        };

        // Focus the editor when the Editor tab is selected; toggle plan-dependent buttons
        SubTabControl.SelectionChanged += (_, _) =>
        {
            if (SubTabControl.SelectedIndex == 0)
            {
                QueryEditor.Focus();
                QueryEditor.TextArea.Focus();
            }
            UpdatePlanTabButtonState();
        };
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
            return (ResultMapper.Map(viewer.CurrentPlan, "query editor", _serverMetadata), viewer);
        }

        // Fallback: find the most recent plan tab
        for (int i = SubTabControl.Items.Count - 1; i >= 0; i--)
        {
            if (SubTabControl.Items[i] is TabItem planTab && planTab.Content is PlanViewerControl v
                && v.CurrentPlan != null)
            {
                return (ResultMapper.Map(v.CurrentPlan, "query editor"), v);
            }
        }

        return (null, null);
    }


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
