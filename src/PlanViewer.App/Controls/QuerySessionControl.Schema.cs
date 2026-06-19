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
    private async Task ShowSchemaInfoAsync(SchemaInfoKind kind)
    {
        if (_contextMenuObject == null || _connectionString == null) return;

        var objectName = _contextMenuObject.FullName;
        SetStatus($"Fetching {kind} for {objectName}...", autoClear: false);

        try
        {
            string content;
            string tabLabel;

            switch (kind)
            {
                case SchemaInfoKind.Indexes:
                    var indexes = await SchemaQueryService.FetchIndexesAsync(_connectionString, objectName);
                    content = DdlScripter.FormatIndexes(objectName, indexes);
                    tabLabel = $"Indexes — {objectName}";
                    break;

                case SchemaInfoKind.TableDefinition:
                    var columns = await SchemaQueryService.FetchColumnsAsync(_connectionString, objectName);
                    var tableIndexes = await SchemaQueryService.FetchIndexesAsync(_connectionString, objectName);
                    content = DdlScripter.FormatColumns(objectName, columns, tableIndexes);
                    tabLabel = $"Table — {objectName}";
                    break;

                case SchemaInfoKind.ObjectDefinition:
                    var definition = await SchemaQueryService.FetchObjectDefinitionAsync(_connectionString, objectName);
                    content = definition ?? $"-- No definition found for {objectName}";
                    tabLabel = $"Definition — {objectName}";
                    break;

                default:
                    return;
            }

            AddSchemaTab(tabLabel, content, isSql: true);
            SetStatus($"Loaded {kind} for {objectName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", autoClear: false);
            Debug.WriteLine($"Schema lookup error: {ex}");
        }
    }

    private void AddSchemaTab(string label, string content, bool isSql)
    {
        var editor = new TextEditor
        {
            Text = content,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            Background = (IBrush)this.FindResource("BackgroundBrush")!,
            Foreground = (IBrush)this.FindResource("ForegroundBrush")!,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Avalonia.Thickness(4)
        };

        if (isSql)
        {
            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            var tm = editor.InstallTextMate(registryOptions);
            tm.SetGrammar(registryOptions.GetScopeByLanguageId("sql"));
        }

        // Context menu for read-only schema tabs
        var schemaCopy = new MenuItem { Header = "Copy" };
        schemaCopy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var sel = editor.TextArea.Selection;
            if (!sel.IsEmpty)
                await clipboard.SetTextAsync(sel.GetText());
        };
        var schemaCopyAll = new MenuItem { Header = "Copy All" };
        schemaCopyAll.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(editor.Text);
        };
        var schemaSelectAll = new MenuItem { Header = "Select All" };
        schemaSelectAll.Click += (_, _) => editor.SelectAll();
        editor.TextArea.ContextMenu = new ContextMenu
        {
            Items = { schemaCopy, schemaCopyAll, new Separator(), schemaSelectAll }
        };

        var headerText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
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
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = editor };
        closeBtn.Tag = tab;
        closeBtn.Click += (s, _) =>
        {
            if (s is Button btn && btn.Tag is TabItem t)
                SubTabControl.Items.Remove(t);
        };

        SubTabControl.Items.Add(tab);
        SubTabControl.SelectedItem = tab;
    }

}
