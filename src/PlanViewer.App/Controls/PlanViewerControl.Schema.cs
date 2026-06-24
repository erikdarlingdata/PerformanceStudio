using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private static bool IsTempObject(string objectName)
    {
        // #temp tables, ##global temp, @table variables, internal worktables
        return objectName.Contains('#') || objectName.Contains('@')
            || objectName.Contains("worktable", StringComparison.OrdinalIgnoreCase)
            || objectName.Contains("worksort", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDataAccessOperator(PlanNode node)
    {
        var op = node.PhysicalOp;
        if (string.IsNullOrEmpty(op)) return false;

        // Modification operators and data access operators reference objects
        return op.Contains("Scan", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Seek", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Lookup", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Insert", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || op.Contains("Spool", StringComparison.OrdinalIgnoreCase);
    }

    private void AddSchemaMenuItems(ContextMenu menu, PlanNode node)
    {
        if (string.IsNullOrEmpty(node.ObjectName) || IsTempObject(node.ObjectName))
            return;
        if (!IsDataAccessOperator(node))
            return;

        var objectName = node.ObjectName;

        menu.Items.Add(new Separator());

        var showIndexes = new MenuItem { Header = $"Show Indexes — {objectName}" };
        showIndexes.Click += async (_, _) => await FetchAndShowSchemaAsync("Indexes", objectName,
            async cs => DdlScripter.FormatIndexes(objectName, await SchemaQueryService.FetchIndexesAsync(cs, objectName)));
        menu.Items.Add(showIndexes);

        var showTableDef = new MenuItem { Header = $"Show Table Definition — {objectName}" };
        showTableDef.Click += async (_, _) => await FetchAndShowSchemaAsync("Table", objectName,
            async cs =>
            {
                var columns = await SchemaQueryService.FetchColumnsAsync(cs, objectName);
                var indexes = await SchemaQueryService.FetchIndexesAsync(cs, objectName);
                return DdlScripter.FormatColumns(objectName, columns, indexes);
            });
        menu.Items.Add(showTableDef);

        // Disable schema items when no connection
        menu.Opening += (_, _) =>
        {
            var enabled = ConnectionString != null;
            showIndexes.IsEnabled = enabled;
            showTableDef.IsEnabled = enabled;
        };
    }

    private async System.Threading.Tasks.Task FetchAndShowSchemaAsync(
        string kind, string objectName, Func<string, System.Threading.Tasks.Task<string>> fetch)
    {
        if (ConnectionString == null) return;

        try
        {
            var content = await fetch(ConnectionString);
            ShowSchemaResult($"{kind} — {objectName}", content);
        }
        catch (Exception ex)
        {
            ShowSchemaResult($"Error — {objectName}", $"-- Error: {ex.Message}");
        }
    }

    private void ShowSchemaResult(string title, string content)
    {
        var editor = new AvaloniaEdit.TextEditor
        {
            Text = content,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            Background = FindBrushResource("BackgroundBrush"),
            Foreground = FindBrushResource("ForegroundBrush"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4)
        };

        // SQL syntax highlighting
        var registryOptions = new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.DarkPlus);
        var tm = editor.InstallTextMate(registryOptions);
        tm.SetGrammar(registryOptions.GetScopeByLanguageId("sql"));

        // Context menu
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var sel = editor.TextArea.Selection;
            if (!sel.IsEmpty)
                await clipboard.SetTextAsync(sel.GetText());
        };
        var copyAllItem = new MenuItem { Header = "Copy All" };
        copyAllItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(editor.Text);
        };
        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) => editor.SelectAll();
        editor.TextArea.ContextMenu = new ContextMenu
        {
            Items = { copyItem, copyAllItem, new Separator(), selectAllItem }
        };

        // Show in a popup window
        var window = new Window
        {
            Title = $"Performance Studio — {title}",
            Width = 700,
            Height = 500,
            MinWidth = 400,
            MinHeight = 200,
            Background = FindBrushResource("BackgroundBrush"),
            Foreground = FindBrushResource("ForegroundBrush"),
            Content = editor
        };

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parentWindow)
        {
            window.Icon = parentWindow.Icon;
            window.Show(parentWindow);
        }
        else
        {
            window.Show();
        }
    }

}
