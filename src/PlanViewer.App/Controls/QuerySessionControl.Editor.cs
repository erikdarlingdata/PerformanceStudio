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
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
    private void SetupSyntaxHighlighting()
    {
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = QueryEditor.InstallTextMate(registryOptions);
        _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId("sql"));
    }

    private void SetupEditorContextMenu()
    {
        var cutItem = new MenuItem { Header = "Cut" };
        cutItem.Click += async (_, _) =>
        {
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            var text = selection.GetText();
            // Only remove the selection once the text has actually reached the
            // clipboard; a failed copy must not destroy the user's text.
            if (await ClipboardHelper.TrySetTextAsync(this, text))
                selection.ReplaceSelectionWithText("");
        };

        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += async (_, _) =>
        {
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            await ClipboardHelper.TrySetTextAsync(this, selection.GetText());
        };

        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += async (_, _) =>
        {
            var text = await ClipboardHelper.TryGetTextAsync(this);
            if (string.IsNullOrEmpty(text)) return;
            QueryEditor.TextArea.PerformTextInput(text);
        };

        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) =>
        {
            QueryEditor.SelectAll();
        };

        var executeFromCursorItem = new MenuItem { Header = "Execute from Cursor" };
        executeFromCursorItem.Click += async (_, _) =>
        {
            var text = GetTextFromCursor();
            if (!string.IsNullOrWhiteSpace(text))
                await CaptureAndShowPlan(estimated: false, queryTextOverride: text);
        };

        var executeCurrentBatchItem = new MenuItem { Header = "Execute Current Batch" };
        executeCurrentBatchItem.Click += async (_, _) =>
        {
            var text = GetCurrentBatch();
            if (!string.IsNullOrWhiteSpace(text))
                await CaptureAndShowPlan(estimated: false, queryTextOverride: text);
        };

        // Schema lookup items
        _schemaSeparator = new Separator();

        _showIndexesItem = new MenuItem { Header = "Show Indexes" };
        _showIndexesItem.Click += async (_, _) => await ShowSchemaInfoAsync(SchemaInfoKind.Indexes);

        _showTableDefItem = new MenuItem { Header = "Show Table Definition" };
        _showTableDefItem.Click += async (_, _) => await ShowSchemaInfoAsync(SchemaInfoKind.TableDefinition);

        _showObjectDefItem = new MenuItem { Header = "Show Object Definition" };
        _showObjectDefItem.Click += async (_, _) => await ShowSchemaInfoAsync(SchemaInfoKind.ObjectDefinition);

        var contextMenu = new ContextMenu
        {
            Items =
            {
                cutItem, copyItem, pasteItem,
                new Separator(), selectAllItem,
                new Separator(), executeFromCursorItem, executeCurrentBatchItem,
                _schemaSeparator,
                _showIndexesItem, _showTableDefItem, _showObjectDefItem
            }
        };

        contextMenu.Opening += OnContextMenuOpening;
        QueryEditor.TextArea.ContextMenu = contextMenu;

        // Move caret to right-click position so schema lookup resolves the clicked object
        QueryEditor.TextArea.PointerPressed += OnEditorPointerPressed;
    }

    private void OnEditorPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueryEditor.TextArea).Properties.IsRightButtonPressed)
            return;

        var pos = QueryEditor.GetPositionFromPoint(e.GetPosition(QueryEditor));
        if (pos == null) return;

        QueryEditor.TextArea.Caret.Position = pos.Value;
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Resolve what object is under the cursor
        var sqlText = QueryEditor.Text;
        var offset = QueryEditor.CaretOffset;
        _contextMenuObject = SqlObjectResolver.Resolve(sqlText, offset);

        var hasConnection = _connectionString != null;
        var hasObject = _contextMenuObject != null && hasConnection;

        _schemaSeparator!.IsVisible = hasObject;
        _showIndexesItem!.IsVisible = hasObject && _contextMenuObject!.Kind is SqlObjectKind.Table or SqlObjectKind.Unknown;
        _showTableDefItem!.IsVisible = hasObject && _contextMenuObject!.Kind is SqlObjectKind.Table or SqlObjectKind.Unknown;
        _showObjectDefItem!.IsVisible = hasObject && _contextMenuObject!.Kind is SqlObjectKind.Function or SqlObjectKind.Procedure;

        // Update headers to show the object name
        if (hasObject)
        {
            var name = _contextMenuObject!.FullName;
            _showIndexesItem.Header = $"Show Indexes — {name}";
            _showTableDefItem.Header = $"Show Table Definition — {name}";
            _showObjectDefItem.Header = $"Show Object Definition — {name}";
        }
    }

    /// <summary>
    /// Whether "Open in Query Editor" has to stop and ask before pasting over the editor.
    ///
    /// <para>Only typed-but-unsaved work earns a prompt: a clean editor is already on disk
    /// (or empty), and a dirty-but-empty one is a buffer the user deleted everything out of —
    /// replacing nothing loses nothing, so both stay as frictionless as they always were.
    /// Split out pure so the decision is testable without a dialog to click, the same trade
    /// CollectOpenTabEntries made for the session-restore list.</para>
    /// </summary>
    internal static bool ReplaceNeedsConfirmation(bool isDirty, string? currentText) =>
        isDirty && !string.IsNullOrEmpty(currentText);

    /* The prompt below puts an await between the dirty check and the replacement, so without
       a latch two back-to-back "Open in Query Editor" clicks would each read the same dirty
       state and stack two Replace prompts — the same reentrancy class the About window's
       update link had. Not data loss (the assignment stays gated on an explicit Replace
       either way), just two dialogs racing; first click wins, the second is a no-op. */
    private bool _replacePromptInFlight;

    /// <summary>
    /// Puts a statement from a plan into the query editor. Internal (like the MainWindow
    /// Click handlers) so tests can drive it without a plan viewer to click through.
    /// </summary>
    internal async void OnOpenInEditorRequested(object? sender, string queryText)
    {
        if (_replacePromptInFlight)
            return;

        /* This used to assign unconditionally — the one wholesale overwrite in the app that
           skipped #462's dirty tracking, so a typed-but-unsaved query was replaced without a
           question. ConfirmationDialog rather than the three-button UnsavedChangesDialog:
           a Save answer here would need the save pipeline, which lives on MainWindow and
           takes the tab — machinery this control has no business growing for one prompt.
           Dismissing the dialog is a no, and a no leaves the editor and the sub-tab alone. */
        if (ReplaceNeedsConfirmation(IsDirty, QueryEditor.Text))
        {
            _replacePromptInFlight = true;
            bool replace;
            try
            {
                replace = await ShowConfirmationDialog(
                    "Unsaved Changes",
                    "The query editor has unsaved changes.\n\nReplace them with this statement? Your current text will be lost.",
                    confirmCaption: "Replace");
            }
            finally
            {
                _replacePromptInFlight = false;
            }

            if (!replace)
                return;
        }

        QueryEditor.Text = queryText;
        SelectEditor();
        QueryEditor.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // F5 or Ctrl+E → Execute (actual plan)
        if ((e.Key == Key.F5 || (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control))
            && ExecuteButton.IsEnabled)
        {
            Execute_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Ctrl+L → Estimated plan
        else if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control
                 && ExecuteEstButton.IsEnabled)
        {
            ExecuteEstimated_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        /* Ctrl+Shift+F → Format.
           The toolbar is a fixed row that scrolls now, and at laptop width Format is one of the
           slots that starts off the right-hand end of it. Every other button in that half either
           has a shortcut already or acts on a plan you have to click to first; Format acts on the
           query you are typing, so reaching it by wheeling the toolbar is the wrong ask.

           Ctrl+Shift+F and not the editors' Shift+Alt+F, which collides with the menu bar: an
           Alt-modified key without Control is an access key as far as AccessKeyHandler is
           concerned, and Alt+F is _File. It survives inside the editor only because this handler
           marks it handled first, so the same keystroke with focus anywhere else in the window
           (a tab header, a plan tab) would open the File menu instead of formatting. Ctrl
           modifiers are skipped by that handler outright, and Ctrl+Shift+O is already this app's
           spelling of Open Query, so the grammar matches. */
        else if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)
                 && FormatButton.IsEnabled)
        {
            Format_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Escape → Cancel running query
        else if (e.Key == Key.Escape && _executionCts != null && !_executionCts.IsCancellationRequested)
        {
            _executionCts.Cancel();
            e.Handled = true;
        }
        /* Ctrl+F4 → close the document being looked at.
           Ctrl+W, the shortcut most apps spell this with, is taken: the window's tunnel handler
           claims it whenever a top-level tab is selected, which is always, and closes that whole
           tab. A tunneled Handled never reaches this bubbling handler, so binding Ctrl+W here
           would do nothing at all — and that is the good outcome, because the alternative is a
           keystroke that sometimes closes a plan and sometimes closes the session it lives in.
           F4 is free: the only F4 in the app is the menu's Alt+F4, and the window's tunnel has no
           case for it, so the keystroke arrives here intact.

           On a view there is nothing selected to close and this does nothing — deliberately
           including not falling through to the top-level tab, which is what Ctrl+W would have
           done and is the surprise this binding exists to avoid. */
        else if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Control)
        {
            if (SelectedDocument is { } document)
            {
                CloseDocument(document);
                e.Handled = true;
            }
        }
    }

    private void OnEditorPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;

        var delta = e.Delta.Y > 0 ? 1 : -1;
        var newSize = QueryEditor.FontSize + delta;
        QueryEditor.FontSize = Math.Clamp(newSize, 7, 52);
        SyncZoomDropdown();
        e.Handled = true;
    }

    private void Zoom_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr
            && int.TryParse(tagStr, out var size))
        {
            QueryEditor.FontSize = size;
        }
    }

    private void SyncZoomDropdown()
    {
        // Find the closest matching zoom level
        var fontSize = (int)Math.Round(QueryEditor.FontSize);
        int bestIdx = 2; // default 100%
        int bestDist = int.MaxValue;

        for (int i = 0; i < ZoomBox.Items.Count; i++)
        {
            if (ZoomBox.Items[i] is ComboBoxItem item && item.Tag is string tagStr
                && int.TryParse(tagStr, out var size))
            {
                var dist = Math.Abs(size - fontSize);
                if (dist < bestDist) { bestDist = dist; bestIdx = i; }
            }
        }

        ZoomBox.SelectionChanged -= Zoom_SelectionChanged;
        ZoomBox.SelectedIndex = bestIdx;
        ZoomBox.SelectionChanged += Zoom_SelectionChanged;
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow == null || string.IsNullOrEmpty(e.Text)) return;

        // If the user types a non-identifier character, let the completion window
        // decide whether to commit (it handles Tab/Enter/Space automatically)
        var ch = e.Text[0];
        if (!char.IsLetterOrDigit(ch) && ch != '_')
        {
            _completionWindow.CompletionList.RequestInsertion(e);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow != null) return;
        if (string.IsNullOrEmpty(e.Text) || !char.IsLetter(e.Text[0])) return;

        var (prefix, wordStart) = GetWordBeforeCaret();
        if (prefix.Length < 2) return;

        var matches = SqlKeywords.All
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0) return;

        _completionWindow = new CompletionWindow(QueryEditor.TextArea);
        _completionWindow.StartOffset = wordStart;
        _completionWindow.Closed += (_, _) => _completionWindow = null;

        foreach (var kw in matches)
            _completionWindow.CompletionList.CompletionData.Add(new SqlCompletionData(kw));

        _completionWindow.Show();
    }

    private string? GetSelectedTextOrNull()
    {
        var selection = QueryEditor.TextArea.Selection;
        if (selection.IsEmpty) return null;
        return selection.GetText();
    }

    private string GetTextFromCursor()
    {
        var doc = QueryEditor.Document;
        var offset = QueryEditor.CaretOffset;
        return doc.GetText(offset, doc.TextLength - offset);
    }

    private string? GetCurrentBatch()
    {
        var doc = QueryEditor.Document;
        var caretOffset = QueryEditor.CaretOffset;
        var text = doc.Text;
        var goPattern = new Regex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var matches = goPattern.Matches(text);

        int batchStart = 0;
        int batchEnd = text.Length;

        foreach (Match m in matches)
        {
            if (m.Index + m.Length <= caretOffset)
            {
                batchStart = m.Index + m.Length;
            }
            else if (m.Index >= caretOffset)
            {
                batchEnd = m.Index;
                break;
            }
        }

        return text[batchStart..batchEnd].Trim();
    }

    /// <summary>How long an ordinary message — progress, or something that worked — stays up.</summary>
    private static readonly TimeSpan StatusClearDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a failure stays up. Longer than an ordinary message, because an error is the one
    /// thing on this strip worth reading twice, but still finite: a message that never clears
    /// outlives the view it was about and ends up hanging over an unrelated one.
    /// </summary>
    private static readonly TimeSpan ErrorStatusClearDelay = TimeSpan.FromSeconds(12);

    private void SetStatus(string text, bool autoClear = true) =>
        ShowStatus(text, isError: false, autoClear ? StatusClearDelay : null);

    /// <summary>
    /// Reports something the session could not do. Red, and gone on its own before long.
    /// </summary>
    private void SetErrorStatus(string text) =>
        ShowStatus(text, isError: true, ErrorStatusClearDelay);

    /// <summary>
    /// Reports a failed operation — unless it failed because the user moved on.
    ///
    /// <para>Cancellation is not a failure worth a word: switching sub-tabs, closing a view, or
    /// starting the next thing tears down whatever was in flight, and the exception that comes
    /// back says "A task was canceled." with no hint of which task or why. That string used to
    /// land in the strip with <c>autoClear: false</c> and sit there across every view the user
    /// visited afterwards. <see cref="TaskCanceledException"/> derives from
    /// <see cref="OperationCanceledException"/>, so the one check covers both.</para>
    /// </summary>
    private void SetStatusFromException(Exception ex, string prefix = "")
    {
        if (ex is OperationCanceledException)
            return;

        SetErrorStatus(prefix + ex.Message);
    }

    /// <summary>
    /// Empties the strip. Called when the active sub-tab changes and when the session leaves the
    /// visual tree, so a message never outlives what it was about.
    /// </summary>
    private void ClearStatus() => ShowStatus("", isError: false, clearAfter: null);

    private void ShowStatus(string text, bool isError, TimeSpan? clearAfter)
    {
        var old = _statusClearCts;
        _statusClearCts = null;
        old?.Cancel();
        old?.Dispose();

        StatusText.Text = text;

        /* Bound rather than assigned so the strip keeps following the theme dictionary, the way
           its XAML foreground always has. */
        StatusText[!TextBlock.ForegroundProperty] =
            new DynamicResourceExtension(isError ? "ErrorBrush" : "ForegroundBrush");

        /* The bar is one line and trims with an ellipsis, so a long message - an error, usually -
           is readable only on hover. Setting the tip to the same text costs nothing when it fits and
           is the difference between a truncated error and a recoverable one when it does not. */
        ToolTip.SetTip(StatusText, string.IsNullOrEmpty(text) ? null : text);

        if (clearAfter is not { } delay || string.IsNullOrEmpty(text))
            return;

        var cts = new CancellationTokenSource();
        _statusClearCts = cts;
        _ = Task.Delay(delay, cts.Token).ContinueWith(_ =>
        {
            /* Re-checked on the UI thread: a status set between the delay completing and this
               posted job running has already cancelled this cts, but cancellation can no longer
               stop a continuation that is past its token check — without the identity test the
               stale timer would wipe the fresh message the moment it was posted. */
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_statusClearCts == cts)
                    ClearStatus();
            });
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
