using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private void ShowParameters(PlanStatement statement)
    {
        ParametersContent.Children.Clear();
        ParametersEmpty.IsVisible = false;

        var parameters = statement.Parameters;

        if (parameters.Count == 0)
        {
            ParametersHeader.Text = "Parameters";
            var localVars = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
            if (localVars.Count > 0)
            {
                // Local variables are still something to say, so this card stays lit.
                AddParameterAnnotation(
                    $"Local variables detected ({string.Join(", ", localVars)}): values not captured in plan XML",
                    "WarningBrush");
                SetInsightQuiet(ParametersHeader, ParametersAccent, false);
            }
            else
            {
                ParametersEmpty.IsVisible = true;
                SetInsightQuiet(ParametersHeader, ParametersAccent, true);
            }
            return;
        }

        ParametersHeader.Text = $"Parameters ({parameters.Count})";
        SetInsightQuiet(ParametersHeader, ParametersAccent, false);

        var allCompiledNull = parameters.All(p => p.CompiledValue == null);
        var hasCompiled = parameters.Any(p => p.CompiledValue != null);
        var hasRuntime = parameters.Any(p => p.RuntimeValue != null);

        /* A 4-column grid: Name | Data Type | Compiled | Runtime, with Compiled and Runtime shown only when at least one
           parameter has that value. The grid is never wider than its card, which cuts what is past its edge with no
           ellipsis; the card sits in a star column of the insights strip and can be as narrow as 200 px. The name column
           shares the card with the value columns at twice a value column's weight and no wider than its longest name
           (BoundParameterNames), so a short name takes its own width and a long one is what gives. The data type sizes to
           its text, and its cells stop at ParameterTypeMaxWidth (AddParamCell). Each value column keeps
           ParameterValueMinWidth however long the names are, so a cut value keeps a hover target. Two value columns at
           their minimum and a data type at its maximum fit the narrowest card. */
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int compiledCol = -1, runtimeCol = -1;
        int nextCol = 2;
        if (hasCompiled)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ParameterValueMinWidth });
            compiledCol = nextCol++;
        }
        if (hasRuntime)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ParameterValueMinWidth });
            runtimeCol = nextCol++;
        }
        // If neither compiled nor runtime, still add one value column for "?"
        if (!hasCompiled && !hasRuntime)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ParameterValueMinWidth });
            compiledCol = nextCol++;
        }

        int rowIndex = 0;

        var columnHeaderBrush = FindBrushResource("InsightParamsBrush");
        var valueBrush = FindBrushResource("ForegroundBrush");
        var missingBrush = FindBrushResource("ErrorBrush");
        var sniffedBrush = FindBrushResource("WarningBrush");

        // Header row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddParamCell(grid, rowIndex, 0, "Parameter", columnHeaderBrush, FontWeight.SemiBold);
        AddParamCell(grid, rowIndex, 1, "Data Type", columnHeaderBrush, FontWeight.SemiBold);
        if (compiledCol >= 0)
            AddParamCell(grid, rowIndex, compiledCol, hasCompiled ? "Compiled" : "Value", columnHeaderBrush, FontWeight.SemiBold);
        if (runtimeCol >= 0)
            AddParamCell(grid, rowIndex, runtimeCol, "Runtime", columnHeaderBrush, FontWeight.SemiBold);
        rowIndex++;

        foreach (var param in parameters)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Name
            AddParamCell(grid, rowIndex, 0, param.Name, valueBrush, FontWeight.SemiBold);

            // Data type
            AddParamCell(grid, rowIndex, 1, param.DataType, valueBrush);

            // Compiled value
            if (compiledCol >= 0)
            {
                var compiledText = param.CompiledValue ?? (allCompiledNull ? "" : "?");
                var compiledBrush = param.CompiledValue != null || allCompiledNull
                    ? valueBrush : missingBrush;
                AddParamCell(grid, rowIndex, compiledCol, compiledText, compiledBrush);
            }

            // Runtime value — amber if it differs from compiled
            if (runtimeCol >= 0)
            {
                var runtimeText = param.RuntimeValue ?? "";
                var sniffed = param.RuntimeValue != null
                    && param.CompiledValue != null
                    && param.RuntimeValue != param.CompiledValue;
                var tooltip = sniffed
                    ? "Runtime value differs from compiled: possible parameter sniffing"
                    : null;
                AddParamCell(grid, rowIndex, runtimeCol, runtimeText,
                    sniffed ? sniffedBrush : valueBrush, tooltip: tooltip);
            }

            rowIndex++;
        }

        ParametersContent.Children.Add(grid);

        /* A plan can load before the viewer is in a window, and the cells take the window's font when it is, so the name
           column's bound is taken now and again when the grid is attached. */
        BoundParameterNames(grid);
        grid.AttachedToVisualTree += (_, _) => BoundParameterNames(grid);

        // Annotations
        if (allCompiledNull && parameters.Count > 0)
        {
            // #579: the phrase inside a string literal or a comment is not a hint.
            var maskedText = PlanAnalyzer.MaskCommentsAndLiterals(statement.StatementText);
            var hasOptimizeForUnknown = statement.StatementText
                .Contains("OPTIMIZE", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(maskedText, @"OPTIMIZE\s+FOR\s+UNKNOWN", RegexOptions.IgnoreCase);

            // RECOMPILE is named only when the statement carries it. Compiled values are also absent
            // when the plan was captured without them, and the card says so rather than naming a
            // hint the statement does not have.
            var hasRecompile = maskedText.Contains("RECOMPILE", StringComparison.OrdinalIgnoreCase);

            if (hasOptimizeForUnknown)
            {
                AddParameterAnnotation(
                    "OPTIMIZE FOR UNKNOWN: optimizer used average density estimates instead of sniffed values",
                    "AccentBrush");
            }
            else if (hasRecompile)
            {
                AddParameterAnnotation(
                    "OPTION(RECOMPILE): parameter values embedded as literals, not sniffed",
                    "WarningBrush");
            }
            else
            {
                AddParameterAnnotation(
                    "Parameter values not captured in the plan: no compiled values, so the values the optimizer sniffed are not known",
                    "WarningBrush");
            }
        }

        var unresolved = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
        if (unresolved.Count > 0)
        {
            AddParameterAnnotation(
                $"Unresolved variables: {string.Join(", ", unresolved)}, not in parameter list",
                "WarningBrush");
        }
    }

    /// <summary>The least a Parameters card value column keeps, in px: a hover target for a cut value's tooltip.</summary>
    private const double ParameterValueMinWidth = 40;

    /// <summary>The most a Parameters card data type cell takes, in px, its margin included.</summary>
    private const double ParameterTypeMaxWidth = 96;

    /// <summary>Bounds the name column at its widest cell on one line, so a short name takes no more than its own width.</summary>
    private static void BoundParameterNames(Grid grid)
    {
        var nameWidth = 0.0;
        foreach (var cell in grid.Children.OfType<TextBlock>().Where(cell => Grid.GetColumn(cell) == 0))
        {
            cell.Measure(Size.Infinity);
            nameWidth = Math.Max(nameWidth, Math.Ceiling(cell.DesiredSize.Width));
        }
        grid.ColumnDefinitions[0].MaxWidth = nameWidth;
    }

    private static void AddParamCell(Grid grid, int row, int col, string text, IBrush brush,
        FontWeight fontWeight = default, string? tooltip = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = fontWeight == default ? FontWeight.Normal : fontWeight,
            Foreground = brush,
            Margin = new Thickness(0, 2, 10, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
            // A trimmed cell is exactly the one whose tip matters, and text with no background
            // only hit-tests its glyphs, so the ellipsis itself would be the only hover target.
            Background = Brushes.Transparent
        };
        // A name is as wide as its column lets it be; a data type's column sizes to its cells, so the cell carries the bound.
        if (col == 0)
            tb.MaxWidth = double.PositiveInfinity;
        else if (col == 1)
            tb.MaxWidth = ParameterTypeMaxWidth - tb.Margin.Right;
        if (tooltip != null)
            ToolTip.SetTip(tb, tooltip);
        else if (text.Length > 30)
            ToolTip.SetTip(tb, text);
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private void AddParameterAnnotation(string text, string brushKey)
    {
        ParametersContent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Foreground = FindBrushResource(brushKey),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
    }

    private static List<string> FindUnresolvedVariables(string queryText, List<PlanParameter> parameters,
        PlanNode? rootNode = null)
    {
        var unresolved = new List<string>();
        if (string.IsNullOrEmpty(queryText))
            return unresolved;

        var extractedNames = new HashSet<string>(
            parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        // Collect table variable names from the plan tree so we don't misreport them as local variables
        var tableVarNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootNode != null)
            CollectTableVariableNames(rootNode, tableVarNames);

        var matches = Regex.Matches(queryText, @"@\w+", RegexOptions.IgnoreCase);
        var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var varName = match.Value;
            if (seenVars.Contains(varName) || extractedNames.Contains(varName))
                continue;
            if (varName.StartsWith("@@", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tableVarNames.Contains(varName))
                continue;

            seenVars.Add(varName);
            unresolved.Add(varName);
        }

        return unresolved;
    }

    private static void CollectTableVariableNames(PlanNode node, HashSet<string> names)
    {
        if (!string.IsNullOrEmpty(node.ObjectName) && node.ObjectName.StartsWith("@"))
        {
            // ObjectName is like "@t.c" — extract the table variable name "@t"
            var dotIdx = node.ObjectName.IndexOf('.');
            var tvName = dotIdx > 0 ? node.ObjectName[..dotIdx] : node.ObjectName;
            names.Add(tvName);
        }
        foreach (var child in node.Children)
            CollectTableVariableNames(child, names);
    }

}
