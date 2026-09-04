using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PlanViewer.App.Services;

internal static partial class AdviceContentBuilder
{
    /// <summary>
    /// Collects consecutive body lines into a single <see cref="SelectableTextBlock"/> (#503).
    ///
    /// <para>Avalonia gives each SelectableTextBlock its own selection and nothing coordinates a drag
    /// that starts in one and ends in another — the whole API is SelectionStart/SelectionEnd/Copy on
    /// the individual control. The advice pane used to add one control per line, so a selection could
    /// never be longer than a line: the reporter could grab a query (one line, one control) but not
    /// Server Context, Parameters, or Missing indexes, which are several lines each. Copy-all into
    /// Notepad was the only way to get a piece of it.</para>
    ///
    /// <para>Merging the lines of a section into one control makes a drag across those lines a normal
    /// selection. Structural elements — headers, warning blocks, operator groups, wait-stat bars,
    /// cards — still flush and stand alone, because they are not text runs and cannot join a text
    /// block. So selection now spans a section's body rather than the entire window; that is the
    /// limit of what the framework will do without a selection manager of our own.</para>
    ///
    /// <para>Indentation moves from control margins to leading spaces. The pane is monospace
    /// throughout, so a space is a reliable unit here in a way it would not be in a proportional
    /// font.</para>
    /// </summary>
    private sealed class BodyTextAccumulator
    {
        private readonly List<Inline> _pending = new();
        private bool _hasLine;

        /// <summary>Roughly one monospace character at the pane's 12px body size.</summary>
        private const double PixelsPerSpace = 6.0;

        public void AddLine(double indentPixels, params Inline[] runs)
        {
            if (_hasLine)
                _pending.Add(new LineBreak());
            _hasLine = true;

            var spaces = (int)(indentPixels / PixelsPerSpace);
            if (spaces > 0)
                _pending.Add(new Run(new string(' ', spaces)));

            _pending.AddRange(runs);
        }

        public void AddLine(double indentPixels, string text, IBrush? foreground) =>
            AddLine(indentPixels, new Run(text) { Foreground = foreground });

        /// <summary>
        /// Folds in the runs a helper already built for a single line — SQL keyword highlighting —
        /// instead of letting that line stand alone as its own block.
        ///
        /// <para>The indent comes from the block the helper built, not from the caller. The helper
        /// expressed it as a Margin, and a Margin is exactly what is lost when its runs move into a
        /// shared block; making the caller restate the number is how the SQL indent got dropped on
        /// the first cut of this.</para>
        /// </summary>
        public void AddLine(SelectableTextBlock built)
        {
            var indentPixels = built.Margin.Left;
            if (built.Inlines is { Count: > 0 })
            {
                // Moved rather than shared: an Inline belongs to one block at a time.
                var taken = built.Inlines.ToArray();
                built.Inlines.Clear();
                AddLine(indentPixels, taken);
            }
            else
            {
                AddLine(indentPixels, built.Text ?? "", built.Foreground);
            }
        }

        /// <summary>
        /// A blank line inside a section stays inside the same block, so a selection can run straight
        /// through it. Only a structural element breaks the block.
        /// </summary>
        public void AddBlankLine()
        {
            if (_hasLine)
                _pending.Add(new LineBreak());
        }

        /// <summary>
        /// Emits everything collected so far as one selectable block, and resets. Safe to call when
        /// nothing is pending — callers flush before every structural element rather than tracking
        /// whether they need to.
        /// </summary>
        public void Flush(Panel panel)
        {
            if (!_hasLine)
            {
                _pending.Clear();
                return;
            }

            var block = new SelectableTextBlock
            {
                FontFamily = MonoFont,
                FontSize = 12,
                Foreground = ValueBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 1)
            };

            foreach (var inline in _pending)
                block.Inlines!.Add(inline);

            panel.Children.Add(block);

            _pending.Clear();
            _hasLine = false;
        }
    }
}
