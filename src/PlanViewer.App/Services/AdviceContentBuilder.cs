using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Services;

/// <summary>
/// Builds styled content for the Advice for Humans window.
/// Shared between MainWindow (file mode) and QuerySessionControl (query mode).
/// </summary>
internal static class AdviceContentBuilder
{
    private static readonly SolidColorBrush HeaderBrush = new(Color.Parse("#4FA3FF"));
    private static readonly SolidColorBrush CriticalBrush = new(Color.Parse("#E57373"));
    private static readonly SolidColorBrush WarningBrush = new(Color.Parse("#FFB347"));
    private static readonly SolidColorBrush InfoBrush = new(Color.Parse("#6BB5FF"));
    private static readonly SolidColorBrush LabelBrush = new(Color.Parse("#9B9EC0"));
    private static readonly SolidColorBrush ValueBrush = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush CodeBrush = new(Color.Parse("#7BCF7B"));
    private static readonly SolidColorBrush MutedBrush = new(Color.Parse("#8B8FA0"));
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

    public static StackPanel Build(string content)
    {
        return Build(content, null);
    }

    public static StackPanel Build(string content, AnalysisResult? analysis)
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

    private static SelectableTextBlock BuildSqlHighlightedLine(string line)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(8, 1, 0, 1)
        };

        int pos = 0;
        var text = line.TrimStart();
        while (pos < text.Length)
        {
            if (!char.IsLetterOrDigit(text[pos]) && text[pos] != '_')
            {
                int start = pos;
                while (pos < text.Length && !char.IsLetterOrDigit(text[pos]) && text[pos] != '_')
                    pos++;
                tb.Inlines!.Add(new Run(text[start..pos]) { Foreground = ValueBrush });
                continue;
            }

            int wordStart = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                pos++;
            var word = text[wordStart..pos];

            if (SqlKeywords.Contains(word))
                tb.Inlines!.Add(new Run(word) { Foreground = SqlKeywordBrush, FontWeight = FontWeight.SemiBold });
            else
                tb.Inlines!.Add(new Run(word) { Foreground = ValueBrush });
        }

        return tb;
    }

    /// <summary>
    /// Creates a warning line with a left accent border for better scannability.
    /// </summary>
    private static Border CreateWarningBlock(string line, SolidColorBrush severityBrush)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(8, 3, 0, 3)
        };

        foreach (var tag in new[] { "[Critical]", "[Warning]", "[Info]" })
        {
            var idx = line.IndexOf(tag);
            if (idx >= 0)
            {
                var afterTag = line[(idx + tag.Length)..].TrimStart();
                // Extract "Type: Message" — show type in severity color, message in value
                var typeColon = afterTag.IndexOf(':');
                if (typeColon > 0)
                {
                    var typeName = afterTag[..typeColon];
                    var message = afterTag[(typeColon + 1)..].TrimStart();
                    tb.Inlines!.Add(new Run(tag + " ")
                        { Foreground = severityBrush, FontWeight = FontWeight.SemiBold });
                    tb.Inlines.Add(new Run(typeName)
                        { Foreground = severityBrush });

                    // Split on unit separator (U+001F) — TextFormatter encodes \n as \x1F
                    // so multi-line messages survive the top-level line split in Build().
                    var messageParts = message.Split('\x1F');
                    for (int p = 0; p < messageParts.Length; p++)
                    {
                        var part = messageParts[p].Trim();
                        if (string.IsNullOrEmpty(part))
                            continue;

                        if (part.StartsWith("Predicate:"))
                        {
                            tb.Inlines.Add(new Run("\n" + part[..10])
                                { Foreground = LabelBrush });
                            tb.Inlines.Add(new Run(part[10..])
                                { Foreground = CodeBrush });
                        }
                        else if (p == 0)
                        {
                            // First line: the main description
                            tb.Inlines.Add(new Run("\n" + part)
                                { Foreground = ValueBrush });
                        }
                        else if (part.StartsWith("\u2022 "))
                        {
                            // Bullet stats: bullet in muted, value in white
                            tb.Inlines.Add(new Run("\n  \u2022 ")
                                { Foreground = MutedBrush });
                            tb.Inlines.Add(new Run(part[2..])
                                { Foreground = ValueBrush });
                        }
                        else if (part.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
                            || part.StartsWith("ON ", StringComparison.OrdinalIgnoreCase)
                            || part.StartsWith("INCLUDE ", StringComparison.OrdinalIgnoreCase)
                            || part.StartsWith("WHERE ", StringComparison.OrdinalIgnoreCase))
                        {
                            // SQL DDL lines (CREATE INDEX, ON, INCLUDE, WHERE)
                            tb.Inlines.Add(new Run("\n" + part)
                                { Foreground = CodeBrush });
                        }
                        else
                        {
                            // Other detail lines
                            tb.Inlines.Add(new Run("\n" + part)
                                { Foreground = MutedBrush });
                        }
                    }
                }
                else
                {
                    tb.Inlines!.Add(new Run(tag)
                        { Foreground = severityBrush, FontWeight = FontWeight.SemiBold });
                    tb.Inlines.Add(new Run(" " + afterTag)
                        { Foreground = ValueBrush });
                }

                return new Border
                {
                    BorderBrush = severityBrush,
                    BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
                    Padding = new Avalonia.Thickness(0),
                    Margin = new Avalonia.Thickness(12, 4, 0, 4),
                    Child = tb
                };
            }
        }

        tb.Text = line.TrimStart();
        tb.Foreground = severityBrush;
        return new Border
        {
            BorderBrush = severityBrush,
            BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
            Padding = new Avalonia.Thickness(0),
            Margin = new Avalonia.Thickness(12, 4, 0, 4),
            Child = tb
        };
    }

    private static SelectableTextBlock CreateOperatorLine(string line)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(8, 2, 0, 0)
        };

        var trimmed = line.TrimStart();
        var parenIdx = trimmed.LastIndexOf('(');

        if (parenIdx > 0)
        {
            var opName = trimmed[..parenIdx].TrimEnd();
            var rest = trimmed[parenIdx..];
            tb.Inlines!.Add(new Run(opName) { Foreground = OperatorBrush, FontWeight = FontWeight.SemiBold });
            tb.Inlines.Add(new Run(" " + rest) { Foreground = MutedBrush });
        }
        else
        {
            tb.Text = trimmed;
            tb.Foreground = OperatorBrush;
            tb.FontWeight = FontWeight.SemiBold;
        }

        return tb;
    }

    /// <summary>
    /// Groups an operator name with its timing line, CPU bar, and stats in a single
    /// container with a purple left accent border for clear visual association.
    /// </summary>
    private static Border CreateOperatorGroup(string operatorLine, string? timingLine, string? statsLine)
    {
        var groupPanel = new StackPanel();

        // Operator name (no extra margin — Border provides it)
        var opTb = CreateOperatorLine(operatorLine);
        opTb.Margin = new Avalonia.Thickness(0);
        groupPanel.Children.Add(opTb);

        // Timing + CPU bar
        if (timingLine != null)
        {
            var timingPanel = CreateOperatorTimingLine(timingLine);
            timingPanel.Margin = new Avalonia.Thickness(4, 2, 0, 0);
            groupPanel.Children.Add(timingPanel);
        }

        // Stats: rows, logical reads, physical reads
        if (statsLine != null)
        {
            groupPanel.Children.Add(new SelectableTextBlock
            {
                Text = statsLine,
                FontFamily = MonoFont,
                FontSize = 12,
                Foreground = MutedBrush,
                Margin = new Avalonia.Thickness(4, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Border
        {
            BorderBrush = OperatorBrush,
            BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
            Padding = new Avalonia.Thickness(8, 2, 0, 2),
            Margin = new Avalonia.Thickness(12, 2, 0, 4),
            Child = groupPanel
        };
    }

    /// <summary>
    /// Renders timing line like "4,616ms CPU (61%), 586ms elapsed (62%)"
    /// with ms values in white and percentages in amber, plus a proportional bar.
    /// </summary>
    private static StackPanel CreateOperatorTimingLine(string trimmed)
    {
        var wrapper = new StackPanel
        {
            Margin = new Avalonia.Thickness(16, 1, 0, 1)
        };

        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        // Split by ", " to get timing parts like "4,616ms CPU (61%)" and "586ms elapsed (62%)"
        var parts = trimmed.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
        int? cpuPct = null;

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                tb.Inlines!.Add(new Run(", ") { Foreground = MutedBrush });

            var part = parts[i].Trim();
            // Extract percentage in parentheses at the end
            var pctStart = part.LastIndexOf('(');
            if (pctStart > 0 && part.EndsWith(")"))
            {
                var timePart = part[..pctStart].TrimEnd();
                var pctPart = part[pctStart..];
                var brush = ValueBrush;
                tb.Inlines!.Add(new Run(timePart) { Foreground = brush });
                tb.Inlines.Add(new Run(" " + pctPart) { Foreground = WarningBrush, FontSize = 11 });

                // Capture CPU percentage for the bar
                if (timePart.Contains("CPU"))
                {
                    var match = CpuPercentRegex.Match(part);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var pctVal))
                        cpuPct = pctVal;
                }
            }
            else
            {
                var brush = ValueBrush;
                tb.Inlines!.Add(new Run(part) { Foreground = brush });
            }
        }

        wrapper.Children.Add(tb);

        // Add proportional CPU bar
        if (cpuPct.HasValue && cpuPct.Value > 0)
        {
            wrapper.Children.Add(new Border
            {
                Width = MaxBarWidth * (cpuPct.Value / 100.0),
                Height = 4,
                Background = AmberBarBrush,
                CornerRadius = new Avalonia.CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(0, 0, 0, 4)
            });
        }

        return wrapper;
    }

    private static StackPanel CreateWaitStatLine(string waitName, string waitValue, double maxWaitMs)
    {
        var wrapper = new StackPanel
        {
            Margin = new Avalonia.Thickness(12, 1, 0, 1)
        };

        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var waitBrush = GetWaitCategoryBrush(waitName);
        tb.Inlines!.Add(new Run(waitName) { Foreground = waitBrush });
        tb.Inlines.Add(new Run(": " + waitValue) { Foreground = ValueBrush });
        wrapper.Children.Add(tb);

        // Proportional bar scaled to max wait in group
        var ms = ParseWaitMs(waitValue);
        if (ms > 0 && maxWaitMs > 0)
        {
            var barWidth = MaxBarWidth * (ms / maxWaitMs);
            wrapper.Children.Add(new Border
            {
                Width = Math.Max(2, barWidth),
                Height = 4,
                Background = waitBrush,
                CornerRadius = new Avalonia.CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(0, 0, 0, 2)
            });
        }

        return wrapper;
    }

    /// <summary>
    /// Renders a missing index impact line like "dbo.Posts (impact: 95%)" with
    /// the table name in value color and the impact colored by severity.
    /// </summary>
    private static SelectableTextBlock CreateMissingIndexImpactLine(string trimmed)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(12, 2, 0, 0)
        };

        var impactStart = trimmed.IndexOf("(impact:");
        var tableName = trimmed[..impactStart].TrimEnd();
        var impactPart = trimmed[impactStart..];

        // Parse the percentage to pick a color
        var pctStr = impactPart.Replace("(impact:", "").Replace("%)", "").Trim();
        var impactBrush = MutedBrush;
        if (double.TryParse(pctStr, out var pct))
        {
            impactBrush = pct >= 70 ? CriticalBrush : (pct >= 40 ? WarningBrush : InfoBrush);
        }

        tb.Inlines!.Add(new Run(tableName + " ") { Foreground = ValueBrush });
        tb.Inlines.Add(new Run(impactPart) { Foreground = impactBrush, FontWeight = FontWeight.SemiBold });

        return tb;
    }

    /// <summary>
    /// Parses a wait stat value like "1,234ms" into a double.
    /// </summary>
    private static double ParseWaitMs(string waitValue)
    {
        var numStr = waitValue.Replace("ms", "").Replace(",", "").Trim();
        return double.TryParse(numStr, out var val) ? val : 0;
    }

    private static SolidColorBrush GetWaitCategoryBrush(string waitType)
    {
        // CPU-related
        if (waitType.StartsWith("SOS_SCHEDULER") || waitType.StartsWith("CXPACKET") ||
            waitType.StartsWith("CXCONSUMER") || waitType == "THREADPOOL" ||
            waitType.StartsWith("EXECSYNC"))
            return new SolidColorBrush(Color.Parse("#FFB347")); // orange

        // I/O-related
        if (waitType.StartsWith("PAGEIOLATCH") || waitType.StartsWith("WRITELOG") ||
            waitType.StartsWith("IO_COMPLETION") || waitType.StartsWith("ASYNC_IO"))
            return new SolidColorBrush(Color.Parse("#E57373")); // red

        // Lock/blocking
        if (waitType.StartsWith("LCK_") || waitType.StartsWith("LOCK"))
            return new SolidColorBrush(Color.Parse("#E57373")); // red

        // Memory
        if (waitType.StartsWith("RESOURCE_SEMAPHORE") || waitType.StartsWith("CMEMTHREAD"))
            return new SolidColorBrush(Color.Parse("#C792EA")); // purple

        // Network
        if (waitType.StartsWith("ASYNC_NETWORK"))
            return new SolidColorBrush(Color.Parse("#6BB5FF")); // blue

        return LabelBrush; // default muted
    }

    /// <summary>
    /// Creates a per-statement triage summary card showing key findings at a glance.
    /// </summary>
    private static Border? CreateTriageSummaryCard(StatementResult stmt)
    {
        var items = new List<(string text, SolidColorBrush brush)>();

        // Parallel efficiency
        var dop = stmt.DegreeOfParallelism;
        if (dop > 1 && stmt.QueryTime != null && stmt.QueryTime.ElapsedTimeMs > 0)
        {
            var cpuMs = (double)stmt.QueryTime.CpuTimeMs;
            var elapsedMs = (double)stmt.QueryTime.ElapsedTimeMs;
            // efficiency = (cpu/elapsed - 1) / (dop - 1) * 100, clamped 0-100
            var ratio = cpuMs / elapsedMs;
            var efficiency = (ratio - 1.0) / (dop - 1.0) * 100.0;
            efficiency = Math.Clamp(efficiency, 0, 100);
            var effBrush = efficiency < 50 ? CriticalBrush : (efficiency < 75 ? WarningBrush : InfoBrush);
            items.Add(($"\u26A0 {efficiency:F0}% parallel efficiency (DOP {dop})", effBrush));
        }

        // Memory grant — color by utilization efficiency
        if (stmt.MemoryGrant != null && stmt.MemoryGrant.GrantedKB > 0)
        {
            var grantedMB = stmt.MemoryGrant.GrantedKB / 1024.0;
            var usedPct = stmt.MemoryGrant.MaxUsedKB > 0
                ? (double)stmt.MemoryGrant.MaxUsedKB / stmt.MemoryGrant.GrantedKB * 100.0
                : 0.0;
            // Red: <10% used (massive waste), Amber: <50%, Blue: <80%, Green-ish (info): >=80%
            var memBrush = usedPct < 10 ? CriticalBrush
                         : usedPct < 50 ? WarningBrush
                         : InfoBrush;
            items.Add(($"Memory grant: {grantedMB:F1} MB ({usedPct:F0}% used)", memBrush));
        }

        // Warning counts by severity
        var criticalCount = stmt.Warnings.Count(w =>
            w.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var warningCount = stmt.Warnings.Count(w =>
            w.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        if (criticalCount > 0 || warningCount > 0)
        {
            var parts = new List<string>();
            if (criticalCount > 0)
                parts.Add($"{criticalCount} critical");
            if (warningCount > 0)
                parts.Add($"{warningCount} warning{(warningCount != 1 ? "s" : "")}");
            var countBrush = criticalCount > 0 ? CriticalBrush : WarningBrush;
            items.Add((string.Join(", ", parts), countBrush));
        }

        // Missing indexes
        if (stmt.MissingIndexes.Count > 0)
        {
            items.Add(($"{stmt.MissingIndexes.Count} missing index suggestion{(stmt.MissingIndexes.Count != 1 ? "s" : "")}", InfoBrush));
        }

        // Spill warnings
        var spillCount = stmt.Warnings.Count(w =>
            w.Type.Contains("Spill", StringComparison.OrdinalIgnoreCase));
        if (spillCount > 0)
        {
            items.Add(($"{spillCount} spill warning{(spillCount != 1 ? "s" : "")}", CriticalBrush));
        }

        if (items.Count == 0)
            return null;

        var cardPanel = new StackPanel
        {
            Margin = new Avalonia.Thickness(4)
        };

        for (int idx = 0; idx < items.Count; idx++)
        {
            var (text, brush) = items[idx];
            var isHeadline = idx == 0;
            cardPanel.Children.Add(new SelectableTextBlock
            {
                Text = text,
                FontFamily = MonoFont,
                FontSize = isHeadline ? 13 : 12,
                FontWeight = isHeadline ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = brush,
                Margin = new Avalonia.Thickness(4, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 4, 8, 4),
            Margin = new Avalonia.Thickness(0, 4, 0, 6),
            Child = cardPanel
        };
    }
}
