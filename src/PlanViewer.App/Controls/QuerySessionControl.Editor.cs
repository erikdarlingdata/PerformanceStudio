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
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            var text = selection.GetText();
            await clipboard.SetTextAsync(text);
            selection.ReplaceSelectionWithText("");
        };

        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            await clipboard.SetTextAsync(selection.GetText());
        };

        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.TryGetTextAsync();
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

    private void OnOpenInEditorRequested(object? sender, string queryText)
    {
        QueryEditor.Text = queryText;
        SubTabControl.SelectedIndex = 0; // Switch to the editor tab
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
        // Escape → Cancel running query
        else if (e.Key == Key.Escape && _executionCts != null && !_executionCts.IsCancellationRequested)
        {
            _executionCts.Cancel();
            e.Handled = true;
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

    private void SetStatus(string text, bool autoClear = true)
    {
        var old = _statusClearCts;
        _statusClearCts = null;
        old?.Cancel();
        old?.Dispose();

        StatusText.Text = text;

        if (autoClear && !string.IsNullOrEmpty(text))
        {
            var cts = new CancellationTokenSource();
            _statusClearCts = cts;
            _ = Task.Delay(3000, cts.Token).ContinueWith(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = "");
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }
}
