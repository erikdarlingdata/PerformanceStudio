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
            var localVars = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
            if (localVars.Count > 0)
            {
                ParametersHeader.Text = "Parameters";
                AddParameterAnnotation(
                    $"Local variables detected ({string.Join(", ", localVars)}) — values not captured in plan XML",
                    "#FFB347");
            }
            else
            {
                ParametersHeader.Text = "Parameters";
                ParametersEmpty.IsVisible = true;
            }
            return;
        }

        ParametersHeader.Text = $"Parameters ({parameters.Count})";

        var allCompiledNull = parameters.All(p => p.CompiledValue == null);
        var hasCompiled = parameters.Any(p => p.CompiledValue != null);
        var hasRuntime = parameters.Any(p => p.RuntimeValue != null);

        // Build a 4-column grid: Name | Data Type | Compiled | Runtime
        // Only show Compiled/Runtime columns if at least one param has that value
        var colDef = "Auto,Auto"; // Name, DataType always shown
        int compiledCol = -1, runtimeCol = -1;
        int nextCol = 2;
        if (hasCompiled)
        {
            colDef += ",*";
            compiledCol = nextCol++;
        }
        if (hasRuntime)
        {
            colDef += ",*";
            runtimeCol = nextCol++;
        }
        // If neither compiled nor runtime, still add one value column for "?"
        if (!hasCompiled && !hasRuntime)
        {
            colDef += ",*";
            compiledCol = nextCol++;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(colDef) };
        int rowIndex = 0;

        // Header row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddParamCell(grid, rowIndex, 0, "Parameter", "#7BCF7B", FontWeight.SemiBold);
        AddParamCell(grid, rowIndex, 1, "Data Type", "#7BCF7B", FontWeight.SemiBold);
        if (compiledCol >= 0)
            AddParamCell(grid, rowIndex, compiledCol, hasCompiled ? "Compiled" : "Value", "#7BCF7B", FontWeight.SemiBold);
        if (runtimeCol >= 0)
            AddParamCell(grid, rowIndex, runtimeCol, "Runtime", "#7BCF7B", FontWeight.SemiBold);
        rowIndex++;

        foreach (var param in parameters)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Name
            AddParamCell(grid, rowIndex, 0, param.Name, "#E4E6EB", FontWeight.SemiBold);

            // Data type
            AddParamCell(grid, rowIndex, 1, param.DataType, "#E4E6EB");

            // Compiled value
            if (compiledCol >= 0)
            {
                var compiledText = param.CompiledValue ?? (allCompiledNull ? "" : "?");
                var compiledColor = param.CompiledValue != null ? "#E4E6EB"
                    : allCompiledNull ? "#E4E6EB" : "#E57373";
                AddParamCell(grid, rowIndex, compiledCol, compiledText, compiledColor);
            }

            // Runtime value — amber if it differs from compiled
            if (runtimeCol >= 0)
            {
                var runtimeText = param.RuntimeValue ?? "";
                var sniffed = param.RuntimeValue != null
                    && param.CompiledValue != null
                    && param.RuntimeValue != param.CompiledValue;
                var runtimeColor = sniffed ? "#FFB347" : "#E4E6EB";
                var tooltip = sniffed
                    ? "Runtime value differs from compiled — possible parameter sniffing"
                    : null;
                AddParamCell(grid, rowIndex, runtimeCol, runtimeText, runtimeColor, tooltip: tooltip);
            }

            rowIndex++;
        }

        ParametersContent.Children.Add(grid);

        // Annotations
        if (allCompiledNull && parameters.Count > 0)
        {
            var hasOptimizeForUnknown = statement.StatementText
                .Contains("OPTIMIZE", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(statement.StatementText, @"OPTIMIZE\s+FOR\s+UNKNOWN", RegexOptions.IgnoreCase);

            if (hasOptimizeForUnknown)
            {
                AddParameterAnnotation(
                    "OPTIMIZE FOR UNKNOWN — optimizer used average density estimates instead of sniffed values",
                    "#6BB5FF");
            }
            else
            {
                AddParameterAnnotation(
                    "OPTION(RECOMPILE) — parameter values embedded as literals, not sniffed",
                    "#FFB347");
            }
        }

        var unresolved = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
        if (unresolved.Count > 0)
        {
            AddParameterAnnotation(
                $"Unresolved variables: {string.Join(", ", unresolved)} — not in parameter list",
                "#FFB347");
        }
    }

    private static void AddParamCell(Grid grid, int row, int col, string text, string color,
        FontWeight fontWeight = default, string? tooltip = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = fontWeight == default ? FontWeight.Normal : fontWeight,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            Margin = new Thickness(0, 2, 10, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200
        };
        // Name and DataType columns are short — no need for max width
        if (col <= 1)
            tb.MaxWidth = double.PositiveInfinity;
        if (tooltip != null)
            ToolTip.SetTip(tb, tooltip);
        else if (text.Length > 30)
            ToolTip.SetTip(tb, text);
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private void AddParameterAnnotation(string text, string color)
    {
        ParametersContent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Foreground = new SolidColorBrush(Color.Parse(color)),
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
