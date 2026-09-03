using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    /* Takes the container-aware entries rather than bare statements (#456 follow-up): the grid
       now lists stored procedure and UDF body statements alongside the outer batch, and a row
       needs to say WHICH module its statement came from or five bare SELECTs are indistinguishable. */
    private void PopulateStatementsGrid(List<StatementWithContainer> statements)
    {
        StatementsHeader.Text = $"Statements ({statements.Count})";

        var hasActualTimes = statements.Any(e => e.Statement.QueryTimeStats != null &&
            (e.Statement.QueryTimeStats.CpuTimeMs > 0 || e.Statement.QueryTimeStats.ElapsedTimeMs > 0));
        var hasUdf = statements.Any(e => e.Statement.QueryUdfElapsedTimeMs > 0);

        // Build columns
        StatementsGrid.Columns.Clear();

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new Avalonia.Data.Binding("Index"),
            Width = new DataGridLength(40),
            IsReadOnly = true
        });

        var queryTemplate = new FuncDataTemplate<StatementRow>((row, _) =>
        {
            if (row == null) return new TextBlock();
            var tb = new TextBlock
            {
                Text = row.QueryText,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 80,
                FontSize = 11,
                Margin = new Thickness(4, 2)
            };
            ToolTip.SetTip(tb, new TextBlock
            {
                Text = row.FullQueryText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            });
            return tb;
        }, supportsRecycling: false);

        StatementsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Query",
            CellTemplate = queryTemplate,
            Width = new DataGridLength(250),
            IsReadOnly = true
        });

        if (hasActualTimes)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "CPU",
                Binding = new Avalonia.Data.Binding("CpuDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.CpuMs)
            });
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Elapsed",
                Binding = new Avalonia.Data.Binding("ElapsedDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.ElapsedMs)
            });
        }

        if (hasUdf)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "UDF",
                Binding = new Avalonia.Data.Binding("UdfDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.UdfMs)
            });
        }

        if (!hasActualTimes)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Est. Cost",
                Binding = new Avalonia.Data.Binding("CostDisplay"),
                Width = new DataGridLength(80),
                IsReadOnly = true,
                CustomSortComparer = new DoubleComparer(r => r.EstCost)
            });
        }

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Critical",
            Binding = new Avalonia.Data.Binding("Critical"),
            Width = new DataGridLength(60),
            IsReadOnly = true
        });

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Warnings",
            Binding = new Avalonia.Data.Binding("Warnings"),
            Width = new DataGridLength(70),
            IsReadOnly = true
        });

        // Build rows
        var rows = new List<StatementRow>();
        for (int i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i].Statement;
            var allWarnings = stmt.PlanWarnings.ToList();
            if (stmt.RootNode != null)
                CollectNodeWarnings(stmt.RootNode, allWarnings);

            var fullText = stmt.StatementText;
            if (string.IsNullOrWhiteSpace(fullText))
                fullText = $"Statement {i + 1}";

            /* A body statement gets its module name in front ("dbo.Proc > SELECT ...") in both
               the cell and its tooltip — display only. Copy/open-in-editor read
               row.Statement.StatementText and hand out the statement exactly as the plan
               recorded it, prefix-free. */
            if (!string.IsNullOrEmpty(statements[i].ContainerPath))
                fullText = $"{statements[i].ContainerPath} > {fullText}";

            var displayText = fullText.Length > 120 ? fullText[..120] + "..." : fullText;

            rows.Add(new StatementRow
            {
                Index = i + 1,
                QueryText = displayText,
                FullQueryText = fullText,
                CpuMs = stmt.QueryTimeStats?.CpuTimeMs ?? 0,
                ElapsedMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0,
                UdfMs = stmt.QueryUdfElapsedTimeMs,
                EstCost = stmt.StatementSubTreeCost,
                Critical = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical),
                Warnings = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Warning),
                Statement = stmt
            });
        }

        StatementsGrid.ItemsSource = rows;
    }

    private void StatementsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is StatementRow row)
            RenderStatement(row.Statement);
    }

    private void StatementsContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        UpdateStatementMenuForSelection();
    }

    /// <summary>
    /// Relabels the statement context menu for the selected statement, and shows the parameterized
    /// variant only when there is a difference between the two forms to choose from (#467).
    ///
    /// <para>Runs when the menu opens rather than when selection changes, so it describes whatever
    /// is selected at the moment it is shown — the same row the two click handlers act on.</para>
    /// </summary>
    private void UpdateStatementMenuForSelection()
    {
        var substitutable = StatementsGrid.SelectedItem is StatementRow row
            && ParameterSubstitution.Apply(row.Statement.StatementText, row.Statement.Parameters)
                .SubstitutionCount > 0;

        // Say so on the label rather than substituting silently: the text the plan records and the
        // text that will run are different things, and which one you just copied matters.
        CopyStatementTextItem.Header = substitutable ? "Copy Query Text (with values)" : "Copy Query Text";
        OpenStatementInEditorItem.Header = substitutable
            ? "Open in Query Editor (with values)"
            : "Open in Query Editor";

        ParameterizedStatementSeparator.IsVisible = substitutable;
        CopyParameterizedStatementTextItem.IsVisible = substitutable;
    }

    private async void CopyStatementText_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is not StatementRow row) return;
        var text = RunnableStatementText(row.Statement);
        if (string.IsNullOrEmpty(text)) return;

        await ClipboardHelper.TrySetTextAsync(this, text);
    }

    /// <summary>
    /// Copies the statement exactly as the plan records it, parameter names and all. Reachable only
    /// when that differs from the substituted form.
    /// </summary>
    private async void CopyParameterizedStatementText_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is not StatementRow row) return;
        var text = row.Statement.StatementText;
        if (string.IsNullOrEmpty(text)) return;

        await ClipboardHelper.TrySetTextAsync(this, text);
    }

    private void OpenInEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is not StatementRow row) return;
        var text = RunnableStatementText(row.Statement);
        if (string.IsNullOrEmpty(text)) return;

        OpenInEditorRequested?.Invoke(this, text);
    }

    /// <summary>
    /// The statement text with the plan's parameter values put back, or the text as-is when the plan
    /// carries no values to put back.
    ///
    /// <para>The plan is the only source used. The query editor's buffer holds the original text in
    /// exactly one case — the user just executed it from that tab — and is wrong for a plan opened
    /// from a file, from Query Store, or from another session.</para>
    /// </summary>
    private static string RunnableStatementText(PlanStatement statement) =>
        ParameterSubstitution.Apply(statement.StatementText, statement.Parameters).Text;

    private static void CollectNodeWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }

    private void ToggleStatements_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsPanel.IsVisible)
            CloseStatementsPanel();
        else
            ShowStatementsPanel();
    }

    private void CloseStatements_Click(object? sender, RoutedEventArgs e)
    {
        CloseStatementsPanel();
    }

    private void ShowStatementsPanel()
    {
        _statementsColumn.Width = new GridLength(450);
        _statementsSplitterColumn.Width = new GridLength(5);
        StatementsSplitter.IsVisible = true;
        StatementsPanel.IsVisible = true;
        StatementsButton.IsVisible = true;
        StatementsButtonSeparator.IsVisible = true;
    }

    private void CloseStatementsPanel()
    {
        StatementsPanel.IsVisible = false;
        StatementsSplitter.IsVisible = false;
        _statementsColumn.Width = new GridLength(0);
        _statementsSplitterColumn.Width = new GridLength(0);
    }
}
