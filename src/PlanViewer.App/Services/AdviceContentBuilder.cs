using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Services;

/// <summary>
/// Builds styled content for the Advice for Humans window.
/// Shared between MainWindow (file mode) and QuerySessionControl (query mode).
/// </summary>
internal static partial class AdviceContentBuilder
{
    private static readonly SolidColorBrush HeaderBrush = new(Color.Parse("#4FA3FF"));
    private static readonly SolidColorBrush CriticalBrush = new(Color.Parse("#E57373"));
    private static readonly SolidColorBrush WarningBrush = new(Color.Parse("#FFB347"));
    private static readonly SolidColorBrush InfoBrush = new(Color.Parse("#6BB5FF"));
    private static readonly SolidColorBrush LabelBrush = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush ValueBrush = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush CodeBrush = new(Color.Parse("#7BCF7B"));
    private static readonly SolidColorBrush MutedBrush = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush OperatorBrush = new(Color.Parse("#C792EA"));
    private static readonly SolidColorBrush SqlKeywordBrush = new(Color.Parse("#569CD6"));
    private static readonly SolidColorBrush SeparatorBrush = new(Color.Parse("#2A2D35"));
    private static readonly SolidColorBrush WarningAccentBrush = new(Color.Parse("#332A1A"));
    private static readonly SolidColorBrush CardBackgroundBrush = new(Color.Parse("#1A2233"));
    private static readonly SolidColorBrush AmberBarBrush = new(Color.Parse("#FFB347"));
    private static readonly SolidColorBrush BlueBarBrush = new(Color.Parse("#4FA3FF"));
    private static readonly FontFamily MonoFont = new("Consolas, Menlo, monospace");

    private const double MaxBarWidth = 200.0;

    private static readonly HashSet<string> PhysicalOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sort", "Filter", "Bitmap", "Hash Match", "Merge Join", "Nested Loops",
        "Stream Aggregate", "Compute Scalar", "Table Scan", "Index Scan",
        "Clustered Index Scan", "Table Spool", "Index Spool", "Constant Scan",
        "Concatenation", "Assert", "Segment", "Sequence Project", "Window Aggregate",
        "Adaptive Join", "Row Count Spool", "Lazy Spool", "Eager Spool",
        "Columnstore Index Scan", "Batch Hash Table Build", "Parallelism",
        "Top", "Index Seek", "Clustered Index Seek", "RID Lookup",
        "Key Lookup", "Clustered Index Update", "Clustered Index Insert",
        "Clustered Index Delete", "Table Insert", "Table Delete"
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS",
        "ON", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL",
        "INSERT", "INTO", "UPDATE", "SET", "DELETE", "MERGE", "USING", "MATCHED",
        "GROUP", "BY", "ORDER", "HAVING", "UNION", "ALL", "EXCEPT", "INTERSECT",
        "TOP", "DISTINCT", "AS", "WITH", "CTE", "OVER", "PARTITION", "ROW_NUMBER",
        "CASE", "WHEN", "THEN", "ELSE", "END", "CAST", "CONVERT", "COALESCE",
        "CREATE", "ALTER", "DROP", "INDEX", "TABLE", "VIEW", "PROCEDURE", "FUNCTION",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "RETURN", "IF", "WHILE",
        "ASC", "DESC", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "APPLY", "PIVOT", "UNPIVOT",
        "NONCLUSTERED", "CLUSTERED", "INCLUDE", "OPTION", "RECOMPILE", "MAXDOP",
        "NOLOCK", "READUNCOMMITTED", "READCOMMITTED", "SERIALIZABLE", "HOLDLOCK"
    };

    private static readonly Regex CpuPercentRegex = new(@"(\d+)%\)", RegexOptions.Compiled);

    // Matches "Node N" or "(Node N)" references in text
    private static readonly Regex NodeRefRegex = new(@"(?<=\(?)\bNode\s+(\d+)\b(?=\)?)", RegexOptions.Compiled);

    private static readonly SolidColorBrush LinkBrush = new(Color.Parse("#4FC3F7"));
    private static readonly Avalonia.Input.Cursor HandCursor = new(Avalonia.Input.StandardCursorType.Hand);

    public static StackPanel Build(string content)
    {
        return Build(content, null, null);
    }

    public static StackPanel Build(string content, AnalysisResult? analysis)
    {
        return Build(content, analysis, null);
    }

    public static StackPanel Build(string content, AnalysisResult? analysis, Action<int>? onNodeClick)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(4, 0) };
        var lines = content.Split('\n');
        var inCodeBlock = false;
        var codeBlockIndent = 0;
        var isStatementText = false;
        var inSubSection = false; // tracks sub-sections within a statement
        var statementIndex = -1; // tracks which statement we're in (0-based)
        var needsTriageCard = false; // inject card on next blank line after SQL text

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // Empty lines — small spacer
            if (string.IsNullOrWhiteSpace(line))
            {
                // Inject triage card on the blank line between SQL text and details
                if (needsTriageCard && analysis != null && statementIndex >= 0
                    && statementIndex < analysis.Statements.Count)
                {
                    var card = CreateTriageSummaryCard(analysis.Statements[statementIndex]);
                    if (card != null)
                        panel.Children.Add(card);
                    needsTriageCard = false;
                }

                panel.Children.Add(new Border { Height = 8 });
                inCodeBlock = false;
                isStatementText = false;
                inSubSection = false;
                continue;
            }

            // Section headers: === ... ===
            if (line.StartsWith("===") && line.EndsWith("==="))
            {
                inCodeBlock = false;
                isStatementText = false;
                inSubSection = false;

                // Strip === markers, just show the text
                var headerText = line.Trim('=', ' ');

                // Add separator before non-first headers
                if (panel.Children.Count > 0)
                {
                    panel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = SeparatorBrush,
                        Margin = new Avalonia.Thickness(0, 10, 0, 6)
                    });
                }

                panel.Children.Add(new SelectableTextBlock
                {
                    Text = headerText,
                    FontFamily = MonoFont,
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = HeaderBrush,
                    Margin = new Avalonia.Thickness(0, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                });

                // Statement text follows "Statement N:"
                if (headerText.StartsWith("Statement"))
                {
                    isStatementText = true;
                    statementIndex++;
                    needsTriageCard = true;
                }

                continue;
            }

            // Statement text (SQL) — highlight keywords
            if (isStatementText)
            {
                panel.Children.Add(BuildSqlHighlightedLine(line));
                continue;
            }

            // Warning lines: [Critical], [Warning], [Info] — with left accent border
            if (line.Contains("[Critical]"))
            {
                panel.Children.Add(CreateWarningBlock(line, CriticalBrush));
                continue;
            }
            if (line.Contains("[Warning]"))
            {
                panel.Children.Add(CreateWarningBlock(line, WarningBrush));
                continue;
            }
            if (line.Contains("[Info]"))
            {
                panel.Children.Add(CreateWarningBlock(line, InfoBrush));
                continue;
            }

            var trimmed = line.TrimStart();

            // Grouped explanation line: "  -> The overestimate may have..."
            if (trimmed.StartsWith("-> "))
            {
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = trimmed[3..],
                    FontFamily = MonoFont,
                    FontSize = 12,
                    FontStyle = Avalonia.Media.FontStyle.Italic,
                    Foreground = MutedBrush,
                    Margin = new Avalonia.Thickness(20, 2, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            // SNIFFING marker
            if (line.Contains("[SNIFFING]"))
            {
                var tb = new SelectableTextBlock
                {
                    FontFamily = MonoFont,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 1, 0, 1)
                };
                var sniffIdx = line.IndexOf("[SNIFFING]");
                tb.Inlines!.Add(new Run(line[..sniffIdx].TrimStart()) { Foreground = ValueBrush });
                tb.Inlines.Add(new Run("[SNIFFING]")
                    { Foreground = CriticalBrush, FontWeight = FontWeight.SemiBold });
                panel.Children.Add(tb);
                continue;
            }

            // Missing index impact line: "dbo.Posts (impact: 95%)"
            if (trimmed.Contains("(impact:") && trimmed.EndsWith("%)"))
            {
                panel.Children.Add(CreateMissingIndexImpactLine(trimmed));
                continue;
            }

            // CREATE INDEX lines (multi-line: CREATE..., ON..., INCLUDE..., WHERE...)
            if (trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                inCodeBlock = true;
                codeBlockIndent = line.Length - trimmed.Length;
            }
            else if (inCodeBlock)
            {
                if (trimmed.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("INCLUDE ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("WHERE ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase))
                { /* still in code block */ }
                else
                    inCodeBlock = false;
            }

            if (inCodeBlock)
            {
                var currentIndent = line.Length - trimmed.Length;
                var displayLine = currentIndent < codeBlockIndent
                    ? new string(' ', codeBlockIndent) + trimmed
                    : line;

                panel.Children.Add(new SelectableTextBlock
                {
                    Text = displayLine,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = CodeBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 1, 0, 1)
                });
                continue;
            }

            // Expensive operators section: highlight operator name + grouped timing + stats
            // Handles both "Operator (Object):" and bare "Sort:" forms
            if (trimmed.EndsWith("):") ||
                (trimmed.EndsWith(":") && PhysicalOperators.Contains(trimmed[..^1])))
            {
                // Peek ahead for timing line and stats line to group with operator
                string? timingLine = null;
                string? statsLine = null;
                var peekIdx = i + 1;
                if (peekIdx < lines.Length)
                {
                    var nextTrimmed = lines[peekIdx].TrimEnd('\r').TrimStart();
                    if ((nextTrimmed.Contains("ms CPU") || nextTrimmed.Contains("ms elapsed"))
                        && nextTrimmed.Length > 0 && char.IsDigit(nextTrimmed[0]))
                    {
                        timingLine = nextTrimmed;
                        peekIdx++;
                    }
                }
                // Stats line: "17,142,169 rows, 4,691,534 logical reads, 884 physical reads"
                if (peekIdx < lines.Length)
                {
                    var nextTrimmed = lines[peekIdx].TrimEnd('\r').TrimStart();
                    if (nextTrimmed.Contains("rows") && nextTrimmed.Length > 0
                        && char.IsDigit(nextTrimmed[0]))
                    {
                        statsLine = nextTrimmed;
                        peekIdx++;
                    }
                }
                i = peekIdx - 1; // skip consumed lines
                panel.Children.Add(CreateOperatorGroup(line, timingLine, statsLine));
                continue;
            }

            // Standalone timing lines (fallback for lines not grouped with an operator)
            if ((trimmed.Contains("ms CPU") || trimmed.Contains("ms elapsed"))
                && trimmed.Length > 0 && char.IsDigit(trimmed[0]))
            {
                panel.Children.Add(CreateOperatorTimingLine(trimmed));
                continue;
            }

            // Sub-section labels within a statement: "Warnings:", "Parameters:", "Wait stats:", etc.
            // These have a space or end with ":" after trimming
            if (IsSubSectionLabel(trimmed))
            {
                inSubSection = true;
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = trimmed,
                    FontFamily = MonoFont,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = LabelBrush,
                    Margin = new Avalonia.Thickness(8, 6, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            // Bullet lines: "   * ..."
            if (trimmed.StartsWith("* "))
            {
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = line,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = MutedBrush,
                    Margin = new Avalonia.Thickness(12, 1, 0, 1),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            // Wait stats lines: "  WAITTYPE: 1,234ms" — color by category with proportional bars
            // Collect entire group, find global max, then render all with consistent bar scaling
            if (trimmed.Contains("ms") && trimmed.Contains(':'))
            {
                var waitColon = trimmed.IndexOf(':');
                if (waitColon > 0 && waitColon < trimmed.Length - 1)
                {
                    var waitName = trimmed[..waitColon];
                    var waitValue = trimmed[(waitColon + 1)..].Trim();
                    if (waitValue.EndsWith("ms") && waitName == waitName.ToUpperInvariant() && !waitName.Contains(' '))
                    {
                        // Collect all wait stat lines in this group
                        var waitGroup = new List<(string name, string value)>
                        {
                            (waitName, waitValue)
                        };
                        while (i + 1 < lines.Length)
                        {
                            var nextLine = lines[i + 1].TrimEnd('\r').TrimStart();
                            if (string.IsNullOrWhiteSpace(nextLine)) break;
                            var nextColon = nextLine.IndexOf(':');
                            if (nextColon <= 0 || nextColon >= nextLine.Length - 1) break;
                            var nextName = nextLine[..nextColon];
                            var nextVal = nextLine[(nextColon + 1)..].Trim();
                            if (!nextVal.EndsWith("ms") || nextName != nextName.ToUpperInvariant()
                                || nextName.Contains(' '))
                                break;
                            waitGroup.Add((nextName, nextVal));
                            i++;
                        }

                        // Find global max for bar scaling
                        var maxWaitMs = 0.0;
                        foreach (var (_, val) in waitGroup)
                        {
                            var ms = ParseWaitMs(val);
                            if (ms > maxWaitMs) maxWaitMs = ms;
                        }

                        // Render all lines with consistent scaling
                        foreach (var (name, val) in waitGroup)
                            panel.Children.Add(CreateWaitStatLine(name, val, maxWaitMs));

                        continue;
                    }
                }
            }

            // Key-value lines: "Label: value"
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && colonIdx < line.Length - 1)
            {
                var labelPart = line[..colonIdx].TrimStart();
                if (labelPart.Length < 40 && !labelPart.Contains('(') && !labelPart.Contains('='))
                {
                    var indent = inSubSection ? 12.0 : 8.0;
                    var tb = new SelectableTextBlock
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(indent, 1, 0, 1)
                    };
                    tb.Inlines!.Add(new Run(labelPart + ":") { Foreground = LabelBrush });
                    tb.Inlines.Add(new Run(line[(colonIdx + 1)..]) { Foreground = ValueBrush });
                    panel.Children.Add(tb);
                    continue;
                }
            }

            // Default: regular text
            panel.Children.Add(new SelectableTextBlock
            {
                Text = line,
                FontFamily = MonoFont,
                FontSize = 12,
                Foreground = ValueBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 1)
            });
        }

        // Post-process: make "Node N" references clickable
        if (onNodeClick != null)
            MakeNodeRefsClickable(panel, onNodeClick);

        return panel;
    }


    private static bool IsSubSectionLabel(string trimmed)
    {
        // "Warnings:", "Parameters:", "Wait stats:", "Operator warnings:",
        // "Missing indexes:", "Expensive operators:"
        if (!trimmed.EndsWith(":")) return false;
        var label = trimmed[..^1];
        // Sub-section labels are short, may contain spaces, and are all-alpha
        return label.Length < 30 && !label.Contains('=') && !label.Contains('(');
    }


}
