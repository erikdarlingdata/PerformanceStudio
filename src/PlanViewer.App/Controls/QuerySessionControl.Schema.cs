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
                    content = FormatIndexes(objectName, indexes);
                    tabLabel = $"Indexes — {objectName}";
                    break;

                case SchemaInfoKind.TableDefinition:
                    var columns = await SchemaQueryService.FetchColumnsAsync(_connectionString, objectName);
                    var tableIndexes = await SchemaQueryService.FetchIndexesAsync(_connectionString, objectName);
                    content = FormatColumns(objectName, columns, tableIndexes);
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

            // Usage stats as a comment
            sb.AppendLine($"-- {ix.SizeMB:N1} MB | Seeks: {ix.UserSeeks:N0} | Scans: {ix.UserScans:N0} | Lookups: {ix.UserLookups:N0} | Updates: {ix.UserUpdates:N0}");

            var withOptions = BuildWithOptions(ix);

            var onPartition = ix.PartitionScheme != null && ix.PartitionColumn != null
                ? $"ON {BracketName(ix.PartitionScheme)}({BracketName(ix.PartitionColumn)})"
                : null;

            if (ix.IsPrimaryKey)
            {
                var clustered = ix.IndexType.Contains("CLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    && !ix.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    ? "CLUSTERED" : "NONCLUSTERED";
                sb.AppendLine($"ALTER TABLE {objectName}");
                sb.AppendLine($"ADD CONSTRAINT {BracketName(ix.IndexName)}");
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
                // Columnstore indexes: no key columns, no INCLUDE, no row/page lock or compression options
                var clustered = ix.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    ? "NONCLUSTERED " : "CLUSTERED ";
                sb.Append($"CREATE {clustered}COLUMNSTORE INDEX {BracketName(ix.IndexName)}");
                sb.AppendLine($" ON {objectName}");

                // Nonclustered columnstore can have a column list
                if (ix.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(ix.KeyColumns))
                {
                    sb.AppendLine($"({ix.KeyColumns})");
                }

                // Only emit non-default options that aren't inherent to columnstore
                var csOptions = BuildColumnstoreWithOptions(ix);
                if (csOptions.Count > 0)
                    sb.AppendLine($"WITH ({string.Join(", ", csOptions)})");

                if (onPartition != null)
                    sb.AppendLine(onPartition);

                // Remove trailing newline before semicolon
                if (sb[sb.Length - 1] == '\n') sb.Length--;
                if (sb[sb.Length - 1] == '\r') sb.Length--;
                sb.AppendLine(";");
            }
            else
            {
                var unique = ix.IsUnique ? "UNIQUE " : "";
                var clustered = ix.IndexType.Contains("CLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    && !ix.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                    ? "CLUSTERED " : "NONCLUSTERED ";
                sb.Append($"CREATE {unique}{clustered}INDEX {BracketName(ix.IndexName)}");
                sb.AppendLine($" ON {objectName}");
                sb.Append($"(");
                sb.Append(ix.KeyColumns);
                sb.AppendLine(")");

                if (!string.IsNullOrEmpty(ix.IncludeColumns))
                    sb.AppendLine($"INCLUDE ({ix.IncludeColumns})");

                if (!string.IsNullOrEmpty(ix.FilterDefinition))
                    sb.AppendLine($"WHERE {ix.FilterDefinition}");

                if (withOptions.Count > 0)
                    sb.AppendLine($"WITH ({string.Join(", ", withOptions)})");

                if (onPartition != null)
                    sb.AppendLine(onPartition);

                // Remove trailing newline before semicolon
                if (sb[sb.Length - 1] == '\n') sb.Length--;
                if (sb[sb.Length - 1] == '\r') sb.Length--;
                sb.AppendLine(";");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool IsColumnstore(IndexInfo ix) =>
        ix.IndexType.Contains("COLUMNSTORE", System.StringComparison.OrdinalIgnoreCase);

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
        if (!string.Equals(ix.DataCompression, "NONE", System.StringComparison.OrdinalIgnoreCase))
            options.Add($"DATA_COMPRESSION = {ix.DataCompression}");

        return options;
    }

    /// <summary>
    /// For columnstore indexes, skip options that are inherent to the storage format
    /// (row/page locks are always OFF, compression is always COLUMNSTORE).
    /// Only emit fill factor and pad index if non-default.
    /// </summary>
    private static List<string> BuildColumnstoreWithOptions(IndexInfo ix)
    {
        var options = new List<string>();

        if (ix.FillFactor > 0 && ix.FillFactor != 100)
            options.Add($"FILLFACTOR = {ix.FillFactor}");
        if (ix.IsPadded)
            options.Add("PAD_INDEX = ON");

        return options;
    }

    private static string FormatColumns(string objectName, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<IndexInfo> indexes)
    {
        if (columns.Count == 0)
            return $"-- No columns found for {objectName}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CREATE TABLE {objectName}");
        sb.AppendLine("(");

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var isLast = i == columns.Count - 1;

            sb.Append($"    {BracketName(col.ColumnName)} ");

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

            // Check if we need a PK constraint after all columns
            var pk = indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
            var needsTrailingComma = !isLast || pk != null;

            sb.AppendLine(needsTrailingComma ? "," : "");
        }

        // Add PK constraint
        var pkIndex = indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pkIndex != null)
        {
            var clustered = pkIndex.IndexType.Contains("CLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                && !pkIndex.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase)
                ? "CLUSTERED " : "NONCLUSTERED ";
            sb.AppendLine($"    CONSTRAINT {BracketName(pkIndex.IndexName)}");
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

        // Add partition scheme from the clustered index (determines table storage)
        var clusteredIx = indexes.FirstOrDefault(ix =>
            ix.IndexType.Contains("CLUSTERED", System.StringComparison.OrdinalIgnoreCase)
            && !ix.IndexType.Contains("NONCLUSTERED", System.StringComparison.OrdinalIgnoreCase));
        if (clusteredIx?.PartitionScheme != null && clusteredIx.PartitionColumn != null)
        {
            sb.AppendLine();
            sb.Append($"ON {BracketName(clusteredIx.PartitionScheme)}({BracketName(clusteredIx.PartitionColumn)})");
        }

        sb.AppendLine(";");

        return sb.ToString();
    }

    private static string BracketName(string name)
    {
        // Already bracketed
        if (name.StartsWith('['))
            return name;
        return $"[{name}]";
    }
}
