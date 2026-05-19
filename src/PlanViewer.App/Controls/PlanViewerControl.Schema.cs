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
            async cs => FormatIndexes(objectName, await SchemaQueryService.FetchIndexesAsync(cs, objectName)));
        menu.Items.Add(showIndexes);

        var showTableDef = new MenuItem { Header = $"Show Table Definition — {objectName}" };
        showTableDef.Click += async (_, _) => await FetchAndShowSchemaAsync("Table", objectName,
            async cs =>
            {
                var columns = await SchemaQueryService.FetchColumnsAsync(cs, objectName);
                var indexes = await SchemaQueryService.FetchIndexesAsync(cs, objectName);
                return FormatColumns(objectName, columns, indexes);
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

    private static string FormatIndexes(string objectName, IReadOnlyList<IndexInfo> indexes)
    {
        if (indexes.Count == 0)
            return $"-- No indexes found on {objectName}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Indexes on {objectName}");
        sb.AppendLine($"-- {indexes.Count} index(es), {indexes[0].RowCount:N0} rows");
        sb.AppendLine();

        foreach (var ix in indexes)
        {
            if (ix.IsDisabled)
                sb.AppendLine("-- ** DISABLED **");

            sb.AppendLine($"-- {ix.SizeMB:N1} MB | Seeks: {ix.UserSeeks:N0} | Scans: {ix.UserScans:N0} | Lookups: {ix.UserLookups:N0} | Updates: {ix.UserUpdates:N0}");

            var withOptions = BuildWithOptions(ix);
            var onPartition = ix.PartitionScheme != null && ix.PartitionColumn != null
                ? $"ON [{ix.PartitionScheme}]([{ix.PartitionColumn}])"
                : null;

            if (ix.IsPrimaryKey)
            {
                var clustered = IsClusteredType(ix) ? "CLUSTERED" : "NONCLUSTERED";
                sb.AppendLine($"ALTER TABLE {objectName}");
                sb.AppendLine($"ADD CONSTRAINT [{ix.IndexName}]");
                sb.Append($"    PRIMARY KEY {clustered} ({ix.KeyColumns})");
                if (withOptions.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append($"    WITH ({string.Join(", ", withOptions)})");
                }
                if (onPartition != null)
                {
                    sb.AppendLine();
                    sb.Append($"    {onPartition}");
                }
                sb.AppendLine(";");
            }
            else if (IsColumnstore(ix))
            {
                var clustered = ix.IndexType.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)
                    ? "NONCLUSTERED " : "CLUSTERED ";
                sb.Append($"CREATE {clustered}COLUMNSTORE INDEX [{ix.IndexName}]");
                sb.AppendLine($" ON {objectName}");
                if (ix.IndexType.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(ix.KeyColumns))
                    sb.AppendLine($"({ix.KeyColumns})");
                var csOptions = BuildColumnstoreWithOptions(ix);
                if (csOptions.Count > 0)
                    sb.AppendLine($"WITH ({string.Join(", ", csOptions)})");
                if (onPartition != null)
                    sb.AppendLine(onPartition);
                TrimTrailingNewline(sb);
                sb.AppendLine(";");
            }
            else
            {
                var unique = ix.IsUnique ? "UNIQUE " : "";
                var clustered = IsClusteredType(ix) ? "CLUSTERED " : "NONCLUSTERED ";
                sb.Append($"CREATE {unique}{clustered}INDEX [{ix.IndexName}]");
                sb.AppendLine($" ON {objectName}");
                sb.AppendLine($"({ix.KeyColumns})");
                if (!string.IsNullOrEmpty(ix.IncludeColumns))
                    sb.AppendLine($"INCLUDE ({ix.IncludeColumns})");
                if (!string.IsNullOrEmpty(ix.FilterDefinition))
                    sb.AppendLine($"WHERE {ix.FilterDefinition}");
                if (withOptions.Count > 0)
                    sb.AppendLine($"WITH ({string.Join(", ", withOptions)})");
                if (onPartition != null)
                    sb.AppendLine(onPartition);
                TrimTrailingNewline(sb);
                sb.AppendLine(";");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatColumns(string objectName, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<IndexInfo> indexes)
    {
        if (columns.Count == 0)
            return $"-- No columns found for {objectName}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CREATE TABLE {objectName}");
        sb.AppendLine("(");

        var pkIndex = indexes.FirstOrDefault(ix => ix.IsPrimaryKey);

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var isLast = i == columns.Count - 1;

            sb.Append($"    [{col.ColumnName}] ");

            if (col.IsComputed && col.ComputedDefinition != null)
            {
                sb.Append($"AS {col.ComputedDefinition}");
            }
            else
            {
                sb.Append(col.DataType);
                if (col.IsIdentity)
                    sb.Append($" IDENTITY({col.IdentitySeed}, {col.IdentityIncrement})");
                sb.Append(col.IsNullable ? " NULL" : " NOT NULL");
                if (col.DefaultValue != null)
                    sb.Append($" DEFAULT {col.DefaultValue}");
            }

            sb.AppendLine(!isLast || pkIndex != null ? "," : "");
        }

        if (pkIndex != null)
        {
            var clustered = IsClusteredType(pkIndex) ? "CLUSTERED " : "NONCLUSTERED ";
            sb.AppendLine($"    CONSTRAINT [{pkIndex.IndexName}]");
            sb.Append($"        PRIMARY KEY {clustered}({pkIndex.KeyColumns})");
            var pkOptions = BuildWithOptions(pkIndex);
            if (pkOptions.Count > 0)
            {
                sb.AppendLine();
                sb.Append($"        WITH ({string.Join(", ", pkOptions)})");
            }
            sb.AppendLine();
        }

        sb.Append(")");

        var clusteredIx = indexes.FirstOrDefault(ix => IsClusteredType(ix) && !IsColumnstore(ix));
        if (clusteredIx?.PartitionScheme != null && clusteredIx.PartitionColumn != null)
        {
            sb.AppendLine();
            sb.Append($"ON [{clusteredIx.PartitionScheme}]([{clusteredIx.PartitionColumn}])");
        }

        sb.AppendLine(";");
        return sb.ToString();
    }

    private static bool IsClusteredType(IndexInfo ix) =>
        ix.IndexType.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
        && !ix.IndexType.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase);

    private static bool IsColumnstore(IndexInfo ix) =>
        ix.IndexType.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildWithOptions(IndexInfo ix)
    {
        var options = new List<string>();
        if (ix.FillFactor > 0 && ix.FillFactor != 100)
            options.Add($"FILLFACTOR = {ix.FillFactor}");
        if (ix.IsPadded)
            options.Add("PAD_INDEX = ON");
        if (!ix.AllowRowLocks)
            options.Add("ALLOW_ROW_LOCKS = OFF");
        if (!ix.AllowPageLocks)
            options.Add("ALLOW_PAGE_LOCKS = OFF");
        if (!string.Equals(ix.DataCompression, "NONE", StringComparison.OrdinalIgnoreCase))
            options.Add($"DATA_COMPRESSION = {ix.DataCompression}");
        return options;
    }

    private static List<string> BuildColumnstoreWithOptions(IndexInfo ix)
    {
        var options = new List<string>();
        if (ix.FillFactor > 0 && ix.FillFactor != 100)
            options.Add($"FILLFACTOR = {ix.FillFactor}");
        if (ix.IsPadded)
            options.Add("PAD_INDEX = ON");
        return options;
    }

    private static void TrimTrailingNewline(System.Text.StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
    }
}
