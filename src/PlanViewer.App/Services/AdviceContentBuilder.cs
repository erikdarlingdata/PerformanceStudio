using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

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
    private static readonly FontFamily MonoFont = new("Consolas, Menlo, monospace");

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

    public static StackPanel Build(string content)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(4, 0) };
        var lines = content.Split('\n');
        var inCodeBlock = false;
        var codeBlockIndent = 0;
        var isStatementText = false;
        var inSubSection = false; // tracks sub-sections within a statement

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // Empty lines — small spacer
            if (string.IsNullOrWhiteSpace(line))
            {
                panel.Children.Add(new Border { Height = 6 });
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
                    isStatementText = true;

                continue;
            }

            // Statement text (SQL) — highlight keywords
            if (isStatementText)
            {
                isStatementText = false;
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
                    Margin = new Avalonia.Thickness(16, 2, 0, 4),
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
                    Margin = new Avalonia.Thickness(8, 1, 0, 1)
                };
                var sniffIdx = line.IndexOf("[SNIFFING]");
                tb.Inlines!.Add(new Run(line[..sniffIdx].TrimStart()) { Foreground = ValueBrush });
                tb.Inlines.Add(new Run("[SNIFFING]")
                    { Foreground = CriticalBrush, FontWeight = FontWeight.SemiBold });
                panel.Children.Add(tb);
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
                    Margin = new Avalonia.Thickness(8, 1, 0, 1)
                });
                continue;
            }

            // Expensive operator timing lines: "4,616ms CPU (61%), 586ms elapsed (62%)"
            // Must start with a digit to avoid catching "Runtime: 1,234ms elapsed, 1,200ms CPU"
            if ((trimmed.Contains("ms CPU") || trimmed.Contains("ms elapsed"))
                && trimmed.Length > 0 && char.IsDigit(trimmed[0]))
            {
                panel.Children.Add(CreateOperatorTimingLine(trimmed));
                continue;
            }

            // Expensive operators section: highlight operator name
            // Handles both "Operator (Object):" and bare "Sort:" forms
            if (trimmed.EndsWith("):") ||
                (trimmed.EndsWith(":") && PhysicalOperators.Contains(trimmed[..^1])))
            {
                panel.Children.Add(CreateOperatorLine(line));
                continue;
            }

            // Sub-section labels within a statement: "Warnings:", "Parameters:", "Wait stats:", etc.
            // These have a space or end with ":" after trimming
            if (IsSubSectionLabel(trimmed))
            {
                inSubSection = true;
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = "  " + trimmed,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = LabelBrush,
                    Margin = new Avalonia.Thickness(0, 6, 0, 2),
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
                    Margin = new Avalonia.Thickness(8, 1, 0, 1),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            // Wait stats lines: "  WAITTYPE: 1,234ms" — color by category
            if (trimmed.Contains("ms") && trimmed.Contains(':'))
            {
                var waitColon = trimmed.IndexOf(':');
                if (waitColon > 0 && waitColon < trimmed.Length - 1)
                {
                    var waitName = trimmed[..waitColon];
                    var waitValue = trimmed[(waitColon + 1)..].Trim();
                    if (waitValue.EndsWith("ms") && waitName == waitName.ToUpperInvariant() && !waitName.Contains(' '))
                    {
                        panel.Children.Add(CreateWaitStatLine(waitName, waitValue, inSubSection));
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
                    var indent = inSubSection ? 8.0 : 0.0;
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
            Margin = new Avalonia.Thickness(4, 1, 0, 1)
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
            Margin = new Avalonia.Thickness(8, 2, 0, 2)
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
                        else if (part.StartsWith("• "))
                        {
                            // Bullet stats: bullet in muted, value in white
                            tb.Inlines.Add(new Run("\n  • ")
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
                    Margin = new Avalonia.Thickness(4, 2, 0, 2),
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
            Margin = new Avalonia.Thickness(4, 2, 0, 2),
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
    /// Renders timing line like "4,616ms CPU (61%), 586ms elapsed (62%)"
    /// with ms values in white and percentages in amber.
    /// </summary>
    private static SelectableTextBlock CreateOperatorTimingLine(string trimmed)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16, 1, 0, 1)
        };

        // Split by ", " to get timing parts like "4,616ms CPU (61%)" and "586ms elapsed (62%)"
        var parts = trimmed.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
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
                var brush = timePart.Contains("CPU") ? ValueBrush : MutedBrush;
                tb.Inlines!.Add(new Run(timePart) { Foreground = brush });
                tb.Inlines.Add(new Run(" " + pctPart) { Foreground = WarningBrush });
            }
            else
            {
                var brush = part.Contains("CPU") ? ValueBrush : MutedBrush;
                tb.Inlines!.Add(new Run(part) { Foreground = brush });
            }
        }

        return tb;
    }

    private static SelectableTextBlock CreateWaitStatLine(string waitName, string waitValue, bool indented)
    {
        var leftMargin = indented ? 16.0 : 8.0;
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(leftMargin, 1, 0, 1)
        };

        var waitBrush = GetWaitCategoryBrush(waitName);
        tb.Inlines!.Add(new Run(waitName) { Foreground = waitBrush });
        tb.Inlines.Add(new Run(": " + waitValue) { Foreground = ValueBrush });

        return tb;
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
}
