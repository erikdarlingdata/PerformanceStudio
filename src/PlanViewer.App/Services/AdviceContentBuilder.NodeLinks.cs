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
    /// Walks all children recursively and replaces "Node N" text with clickable inline links.
    /// </summary>
    private static void MakeNodeRefsClickable(Panel panel, Action<int> onNodeClick)
    {
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];

            // Recurse into containers
            if (child is Panel innerPanel)
            {
                MakeNodeRefsClickable(innerPanel, onNodeClick);
                continue;
            }
            if (child is Border border)
            {
                if (border.Child is Panel borderPanel)
                {
                    MakeNodeRefsClickable(borderPanel, onNodeClick);
                    continue;
                }
                if (border.Child is SelectableTextBlock borderStb)
                {
                    if (borderStb.Inlines?.Count > 0)
                        ProcessInlines(borderStb, onNodeClick);
                    else if (!string.IsNullOrEmpty(borderStb.Text) && NodeRefRegex.IsMatch(borderStb.Text))
                    {
                        var bText = borderStb.Text;
                        var bFg = borderStb.Foreground;
                        borderStb.Text = null;
                        AddRunsWithNodeLinks(borderStb.Inlines!, bText, bFg, onNodeClick);
                        WireNodeClickHandler(borderStb, onNodeClick);
                    }
                    continue;
                }
            }
            if (child is Expander expander && expander.Content is Panel expanderPanel)
            {
                MakeNodeRefsClickable(expanderPanel, onNodeClick);
                continue;
            }

            // Process SelectableTextBlock with Inlines
            if (child is SelectableTextBlock stb && stb.Inlines?.Count > 0)
            {
                ProcessInlines(stb, onNodeClick);
                continue;
            }

            // Process SelectableTextBlock with plain Text
            if (child is SelectableTextBlock stbPlain && stbPlain.Inlines?.Count == 0
                && !string.IsNullOrEmpty(stbPlain.Text) && NodeRefRegex.IsMatch(stbPlain.Text))
            {
                var text = stbPlain.Text;
                var fg = stbPlain.Foreground;
                stbPlain.Text = null;
                AddRunsWithNodeLinks(stbPlain.Inlines!, text, fg, onNodeClick);
                WireNodeClickHandler(stbPlain, onNodeClick);
            }
        }
    }

    /// <summary>
    /// Processes existing Inlines in a SelectableTextBlock, splitting any Run that
    /// contains "Node N" into segments with clickable links.
    /// </summary>
    private static void ProcessInlines(SelectableTextBlock stb, Action<int> onNodeClick)
    {
        var inlines = stb.Inlines!;
        var snapshot = inlines.ToList();
        var changed = false;

        foreach (var inline in snapshot)
        {
            if (inline is Run run && !string.IsNullOrEmpty(run.Text) && NodeRefRegex.IsMatch(run.Text))
            {
                changed = true;
                break;
            }
        }

        if (!changed) return;

        // Rebuild inlines
        var newInlines = new List<Avalonia.Controls.Documents.Inline>();
        foreach (var inline in snapshot)
        {
            if (inline is Run run && !string.IsNullOrEmpty(run.Text) && NodeRefRegex.IsMatch(run.Text))
            {
                var text = run.Text;
                int pos = 0;
                foreach (System.Text.RegularExpressions.Match m in NodeRefRegex.Matches(text))
                {
                    if (m.Index > pos)
                        newInlines.Add(new Run(text[pos..m.Index]) { Foreground = run.Foreground, FontWeight = run.FontWeight, FontSize = run.FontSize > 0 ? run.FontSize : double.NaN });

                    if (int.TryParse(m.Groups[1].Value, out var nodeId))
                    {
                        var linkRun = new Run(m.Value)
                        {
                            Foreground = LinkBrush,
                            TextDecorations = Avalonia.Media.TextDecorations.Underline,
                            FontWeight = run.FontWeight,
                            FontSize = run.FontSize > 0 ? run.FontSize : double.NaN
                        };
                        newInlines.Add(linkRun);
                    }
                    else
                    {
                        newInlines.Add(new Run(m.Value) { Foreground = run.Foreground, FontWeight = run.FontWeight });
                    }
                    pos = m.Index + m.Length;
                }
                if (pos < text.Length)
                    newInlines.Add(new Run(text[pos..]) { Foreground = run.Foreground, FontWeight = run.FontWeight, FontSize = run.FontSize > 0 ? run.FontSize : double.NaN });
            }
            else
            {
                newInlines.Add(inline);
            }
        }

        inlines.Clear();
        foreach (var ni in newInlines)
            inlines.Add(ni);

        // Wire up PointerPressed on the TextBlock to detect clicks on link runs
        WireNodeClickHandler(stb, onNodeClick);
    }

    /// <summary>
    /// Splits plain text into Runs, making "Node N" references clickable.
    /// </summary>
    private static void AddRunsWithNodeLinks(InlineCollection inlines, string text, IBrush? defaultFg, Action<int> onNodeClick)
    {
        int pos = 0;
        var stb = inlines.FirstOrDefault()?.Parent as SelectableTextBlock;
        foreach (System.Text.RegularExpressions.Match m in NodeRefRegex.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]) { Foreground = defaultFg });

            if (int.TryParse(m.Groups[1].Value, out _))
            {
                inlines.Add(new Run(m.Value)
                {
                    Foreground = LinkBrush,
                    TextDecorations = Avalonia.Media.TextDecorations.Underline
                });
            }
            else
            {
                inlines.Add(new Run(m.Value) { Foreground = defaultFg });
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]) { Foreground = defaultFg });

        // Find the parent SelectableTextBlock to attach click handler
        // The inlines collection is owned by the SelectableTextBlock that called us
        // We need to wire it up after — caller should call WireNodeClickHandler separately
    }

    /// <summary>
    /// Attaches a PointerPressed handler to a SelectableTextBlock that detects clicks
    /// on underlined "Node N" text and invokes the callback.
    /// Uses Tunnel routing so the handler fires before SelectableTextBlock's
    /// built-in text selection consumes the event.
    /// </summary>
    private static void WireNodeClickHandler(SelectableTextBlock stb, Action<int> onNodeClick)
    {
        stb.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            var point = e.GetPosition(stb);
            var hit = stb.TextLayout.HitTestPoint(point);
            if (!hit.IsInside) return;

            var run = RunAtCharIndex(stb, hit.TextPosition);
            if (run?.Text == null
                || run.TextDecorations != Avalonia.Media.TextDecorations.Underline
                || run.Foreground != LinkBrush)
                return;

            var m = NodeRefRegex.Match(run.Text);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var nodeId))
                return;

            e.Handled = true;

            // Clear any text selection and release pointer capture
            // to prevent SelectableTextBlock from starting a selection drag
            stb.SelectionStart = 0;
            stb.SelectionEnd = 0;
            e.Pointer.Capture(null);

            onNodeClick(nodeId);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Change cursor on hover over link runs
        stb.PointerMoved += (_, e) =>
        {
            var point = e.GetPosition(stb);
            var hit = stb.TextLayout.HitTestPoint(point);
            if (!hit.IsInside)
            {
                stb.Cursor = Avalonia.Input.Cursor.Default;
                return;
            }

            var run = RunAtCharIndex(stb, hit.TextPosition);
            stb.Cursor = run != null
                && run.TextDecorations == Avalonia.Media.TextDecorations.Underline
                && run.Foreground == LinkBrush
                ? HandCursor
                : Avalonia.Input.Cursor.Default;
        };
    }

    /// <summary>
    /// The <see cref="Run"/> covering <paramref name="charIndex"/> in the block's laid-out text, or
    /// null when the index falls outside every Run.
    ///
    /// <para>Walking Runs alone was correct while every block held one line. #503 merges a section's
    /// lines into a single block separated by LineBreaks, and a LineBreak takes up a position in the
    /// laid-out text while exposing no Text of its own — so skipping it slides every later Run's
    /// offset backwards, and a click on a "Node N" past the first line resolves to the wrong run or
    /// to none.</para>
    ///
    /// <para>Rather than assume what a non-Run inline contributes, each Run is located in the text
    /// the collection actually laid out, searching forward from the end of the previous one. That is
    /// exact for any interleaving — a LineBreak today, an InlineUIContainer tomorrow — where
    /// distributing the leftover characters evenly across non-Runs would only hold while they are all
    /// the same kind, and would go quietly wrong rather than loudly the day they are not. Scanning
    /// forward rather than from zero is also what keeps repeated text (two runs both reading
    /// "Node 4") landing on the right one.</para>
    ///
    /// <para>Internal so a test can check the offset math directly: driving it through a real click
    /// would mean placing a pointer at a laid-out coordinate, which tests the font more than the
    /// arithmetic that was wrong.</para>
    /// </summary>
    internal static Run? RunAtCharIndex(SelectableTextBlock stb, int charIndex)
    {
        var inlines = stb.Inlines;
        if (inlines == null)
            return null;

        var text = inlines.Text ?? "";
        var cursor = 0;

        foreach (var inline in inlines)
        {
            if (inline is not Run run || string.IsNullOrEmpty(run.Text))
                continue;

            var start = text.IndexOf(run.Text, cursor, StringComparison.Ordinal);
            if (start < 0)
                return null;

            var end = start + run.Text.Length;
            if (charIndex >= start && charIndex < end)
                return run;

            cursor = end;
        }

        return null;
    }
}
