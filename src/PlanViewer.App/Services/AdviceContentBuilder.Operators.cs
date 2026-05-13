using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Services;

internal static partial class AdviceContentBuilder
{
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
}
