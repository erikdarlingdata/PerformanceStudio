using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Services;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Dialogs;

/// <summary>
/// The plan comparison, as a diff.
///
/// <para><b>What this replaced.</b> The comparison used to go through the advice window, which
/// meant a monospace dump: every metric line in the same plain white, "99.3% cheaper" rendered
/// identically to "1,386% slower", and wait statistics as two stacked lists a reader had to
/// cross-reference by eye. The data was already a diff. Nothing on screen said so.</para>
///
/// <para><b>Where the numbers come from.</b> <see cref="ComparisonFormatter.Build"/>, the same
/// structure the text report renders from — so this window cannot invent a percentage the report
/// disagrees with, and Copy report hands back the report for exactly the comparison on screen.
/// Nothing here decides whether a change is good: every colour follows
/// <see cref="ComparisonDirection"/>, which is settled per metric in Core.</para>
///
/// <para>Code-built rather than XAML, like the rest of this folder, and with no view model: the
/// content is built once from a result that never changes while the window is open.</para>
/// </summary>
public static class ComparisonWindow
{
    /// <summary>
    /// Builds and shows the comparison. Non-modal, owned by <paramref name="owner"/>, so a reader
    /// can keep it up beside the plans it describes.
    /// </summary>
    public static void Show(
        Window owner,
        AnalysisResult planA, AnalysisResult planB,
        string labelA, string labelB)
    {
        var result = ComparisonFormatter.Build(planA, planB, labelA, labelB);
        var theme = new Palette(owner);

        var body = new StackPanel { Spacing = 12 };

        if (result.Statements.Count == 0)
        {
            // What the report says in the same situation. A window that answers Compare with a
            // header and an empty expanse is the same defect as a button that answers with nothing.
            body.Children.Add(new TextBlock
            {
                Text = "No statements to compare.",
                FontSize = 12,
                Foreground = theme.Brush("ForegroundMutedBrush")
            });
        }

        foreach (var statement in result.Statements)
            body.Children.Add(BuildStatementCard(statement, theme));

        var scroller = new ScrollViewer
        {
            Content = body,
            Padding = new Avalonia.Thickness(0, 10, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        // Ctrl+Wheel font scaling, which the advice window this replaced had. Losing it would be a
        // regression for anyone who had been zooming the comparison report.
        var scale = new ScaleTransform(1, 1);
        var zoomHost = new LayoutTransformControl { LayoutTransform = scale, Child = scroller };

        var buttonTheme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!;

        var copyButton = new Button
        {
            Content = CopyCaption,
            Height = 32,
            MinWidth = 88,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = buttonTheme
        };

        var closeButton = new Button
        {
            Content = "Close",
            Height = 32,
            MinWidth = 88,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = buttonTheme
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            Children = { copyButton, closeButton }
        };

        var panel = new DockPanel { Margin = new Avalonia.Thickness(12) };
        var header = BuildHeader(result, theme);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(buttonRow);
        panel.Children.Add(zoomHost);

        var window = new Window
        {
            Title = "Performance Studio — Plan Comparison",
            // Wider than the advice window it replaced: four columns of numbers, not one of prose.
            Width = 920,
            Height = 680,
            MinWidth = 620,
            MinHeight = 380,
            Icon = owner.Icon,
            Background = theme.Brush("BackgroundBrush"),
            Foreground = theme.Brush("ForegroundBrush"),
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var zoom = 1.0;
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
        {
            if (!args.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            args.Handled = true;
            zoom = Math.Clamp(zoom + (args.Delta.Y > 0 ? 0.1 : -0.1), 0.5, 3.0);
            scale.ScaleX = zoom;
            scale.ScaleY = zoom;
        }, RoutingStrategies.Tunnel);

        /* Rendered from the result already in hand rather than re-comparing the two plans: the
           bytes handed to the clipboard then describe what is on the screen by construction, and a
           click does not re-run the comparison on the UI thread.

           The generation counter is what stops a second click's "Copied!" being wiped by the first
           click's 1.5s timer, which is a real race in the peer this was modelled on. */
        var copyGeneration = 0;
        copyButton.Click += async (_, _) =>
        {
            var generation = ++copyGeneration;

            copyButton.Content = await ClipboardHelper.TrySetTextAsync(window, ComparisonFormatter.Compare(result))
                ? "Copied!"
                : "Clipboard busy - try again";

            await Task.Delay(1500);

            if (generation == copyGeneration)
                copyButton.Content = CopyCaption;
        };

        closeButton.Click += (_, _) => window.Close();

        window.Show(owner);
    }

    private const string CopyCaption = "Copy report";

    // --- Header --------------------------------------------------------------------------------

    /// <summary>
    /// What A and B are, then the answer. In that order because the verdict speaks of Plan A and
    /// Plan B and would otherwise name two things the reader has not been introduced to.
    ///
    /// <para>There is no "Plan Comparison" heading: the title bar already says it, and a 16px
    /// restatement of something the reader has read would be the largest thing on a window whose
    /// job is to deliver one sentence.</para>
    /// </summary>
    private static Control BuildHeader(ComparisonResult result, Palette theme)
    {
        var stack = new StackPanel();

        stack.Children.Add(PlanIdentity("A", result.LabelA, theme, bottomGap: 4));
        stack.Children.Add(PlanIdentity("B", result.LabelB, theme, bottomGap: 0));

        if (result.Verdict != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = result.Verdict.Text,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme.DirectionTextBrush(result.Verdict.Direction)
            });
        }

        if (result.EstimatedPlanNote != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = result.EstimatedPlanNote,
                FontSize = 12,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
                Foreground = theme.Brush("WarningBrush")
            });
        }

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = theme.Brush("BorderBrush"),
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        });

        return stack;
    }

    /// <summary>
    /// "A" or "B" in a boxed letter, then the tab label it stands for — so every "Plan A" column
    /// header below has one place to resolve to. The letter carries full foreground weight because
    /// it is the key, not a caption for one.
    /// </summary>
    private static Control PlanIdentity(string letter, string label, Palette theme, double bottomGap) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 0, 0, bottomGap),
            Children =
            {
                new Border
                {
                    BorderBrush = theme.Brush("ForegroundMutedBrush"),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Width = 20,
                    Padding = new Avalonia.Thickness(0, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = letter,
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = theme.Brush("ForegroundBrush")
                    }
                },
                new SelectableTextBlock
                {
                    Text = label,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = theme.Brush("ForegroundBrush"),
                    // Null background hit-tests glyphs only, so a press in the gaps between
                    // characters would start no selection at all.
                    Background = Brushes.Transparent
                }
            }
        };

    // --- Statement cards -----------------------------------------------------------------------

    private static Control BuildStatementCard(ComparisonStatement statement, Palette theme)
    {
        var stack = new StackPanel { Spacing = 8 };

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = $"Statement {statement.Index}",
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = theme.Brush("ForegroundBrush")
                }
            }
        };

        title.Children.Add(statement.Presence switch
        {
            StatementPresence.OnlyInA => Chip("only in Plan A", ComparisonDirection.Neutral, theme),
            StatementPresence.OnlyInB => Chip("only in Plan B", ComparisonDirection.Neutral, theme),
            _ => Chip(NetSummary(statement), statement.NetDirection, theme)
        });

        stack.Children.Add(title);

        stack.Children.Add(new SelectableTextBlock
        {
            Text = CollapseWhitespace(statement.StatementText),
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.Brush("ForegroundMutedBrush"),
            Background = Brushes.Transparent
        });

        if (statement.Presence == StatementPresence.Both)
        {
            // Worst regression first: the reason a comparison gets opened is almost always the row
            // that got worse, and report order buries it under whatever happens to be printed first.
            var sections = new List<(string Header, IReadOnlyList<DiffRow> Rows)>
            {
                ("Metric", ComparisonOrdering.WorstFirst(statement.Metrics)
                    .Select(metric => new DiffRow(
                        metric.Label, metric.DisplayA, metric.DisplayB,
                        DeltaChipText(metric.DeltaLabel, metric.Direction, metric.DeltaPercent),
                        metric.Direction))
                    .ToList())
            };

            if (statement.Waits.Count > 0)
            {
                sections.Add(("Wait type", statement.Waits
                    .Select(wait => new DiffRow(
                        wait.WaitType, wait.DisplayA, wait.DisplayB,
                        DeltaChipText(wait.DeltaLabel, wait.Direction, wait.DeltaPercent),
                        wait.Direction))
                    .ToList()));
            }

            stack.Children.Add(BuildTable(sections, theme));
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = "The other plan has no counterpart for this statement, so there is nothing to compare it against.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme.Brush("ForegroundMutedBrush")
            });
        }

        return new Border
        {
            Background = theme.Brush("BackgroundLightBrush"),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(12),
            /* Heavy on the left, hairline everywhere else. The left edge is the card's verdict,
               readable before any of the numbers are; the hairline is what makes the card a card at
               all, because BackgroundLightBrush against the window ground is a 1.1:1 step and an
               unchanged statement would otherwise be loose text on a page. */
            BorderThickness = new Avalonia.Thickness(3, 1, 1, 1),
            BorderBrush = theme.DirectionBrush(statement.NetDirection),
            Child = stack
        };
    }

    /// <summary>
    /// The statement text arrives flattened to one line, which turns the original SQL's indentation
    /// into runs of a dozen spaces mid-sentence. The report prints it that way and keeps doing so;
    /// on a card it just makes the query hard to read.
    /// </summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NetSummary(ComparisonStatement statement) =>
        statement.NetDirection switch
        {
            ComparisonDirection.Better => $"improved on {statement.NetBasis}",
            ComparisonDirection.Worse => $"regressed on {statement.NetBasis}",
            _ => $"unchanged on {statement.NetBasis}"
        };

    // --- The four-column table -----------------------------------------------------------------

    /// <summary>One line of a card, whatever it is a line about.</summary>
    private readonly record struct DiffRow(
        string Name, string A, string B, string? Delta, ComparisonDirection Direction);

    /// <summary>
    /// Name, Plan A, Plan B, change chip.
    ///
    /// <para><b>One grid, not one per section.</b> Metrics and waits started as two grids, each
    /// sizing its own name column, and RESERVED_MEMORY_ALLOCATION_EXT in the lower one put its
    /// numbers 120px right of the numbers directly above them — two tables in one card that did not
    /// line up. A shared-size scope is the textbook fix and did not take here; one grid with a
    /// header row per section makes the question impossible to get wrong instead.</para>
    ///
    /// <para><b>Why the name column is the elastic one.</b> Everything else has a floor it must
    /// keep: the value columns need room for a fifteen-character cost, and the chip is the whole
    /// point of the row. At the window's 620px minimum something has to give, and a wait type
    /// ellipsised at 90px is a far smaller loss than a clipped delta — horizontal scrolling is off,
    /// so an overrun would not even be reachable.</para>
    /// </summary>
    private static Control BuildTable(
        IReadOnlyList<(string Header, IReadOnlyList<DiffRow> Rows)> sections, Palette theme)
    {
        var grid = new Grid
        {
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            ColumnDefinitions =
            [
                // Capped so a wide window does not strand the numbers half a screen from their names.
                new ColumnDefinition(GridLength.Star) { MinWidth = 90, MaxWidth = 280 },
                // Auto with a floor rather than fixed: a long value pushes the column instead of
                // spilling leftward over the name, which is what a fixed width would let it do.
                new ColumnDefinition(GridLength.Auto) { MinWidth = 110 },
                new ColumnDefinition(GridLength.Auto) { MinWidth = 110 },
                new ColumnDefinition(GridLength.Auto)
            ]
        };

        foreach (var (header, rows) in sections)
        {
            // Section headers after the first get real air above them; the first sits under the
            // SQL, which the card's own spacing has already separated.
            var leading = grid.RowDefinitions.Count == 0 ? 0 : 16;

            AddRow(grid, leading, 6,
                Header(header, theme),
                Header("Plan A", theme, alignRight: true),
                Header("Plan B", theme, alignRight: true),
                Header("Change", theme));

            foreach (var row in rows)
            {
                AddRow(grid, 0, 3,
                    new TextBlock
                    {
                        Text = row.Name,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Avalonia.Thickness(0, 0, 16, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = theme.Brush("ForegroundMutedBrush")
                    },
                    Value(row.A, theme),
                    Value(row.B, theme),
                    // No cell at all rather than an empty TextBlock: Grid children may be sparse,
                    // and a blank text block still measures a line box of whatever size it
                    // inherited, which shows up as an irregular row pitch in a column of numbers.
                    row.Delta == null ? null : Chip(row.Delta, row.Direction, theme));
            }
        }

        return grid;
    }

    private static void AddRow(Grid grid, double leading, double trailing, params Control?[] cells)
    {
        var index = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int column = 0; column < cells.Length; column++)
        {
            if (cells[column] is not { } cell) continue;

            cell.Margin = new Avalonia.Thickness(cell.Margin.Left, leading, cell.Margin.Right, trailing);
            Grid.SetRow(cell, index);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private static Control Header(string text, Palette theme, bool alignRight = false) =>
        new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 0, alignRight ? 12 : 16, 0),
            Foreground = theme.Brush("ForegroundMutedBrush")
        };

    /// <summary>
    /// Right-aligned so Plan A's digits line up under each other and Plan B's under those, which is
    /// what makes a column of numbers scannable without reading any of them.
    /// </summary>
    private static Control Value(string text, Palette theme) =>
        new SelectableTextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = theme.Brush("ForegroundBrush"),
            Background = Brushes.Transparent
        };

    /// <summary>
    /// The delta, as an outlined pill in the status colour. Outlined rather than filled: a solid
    /// red block per regressed row would make a card with four regressions unreadable, and the
    /// theme has no soft variant of the status colours to fill with — inventing one here is exactly
    /// the drift the token file forbids.
    ///
    /// <para>Edge and text are one brush, so a neutral pill is drawn in the muted foreground rather
    /// than in BorderBrush: a line colour at 1.4:1 on the card surface is a pill nobody can see,
    /// which leaves the text looking like a stray phrase with odd padding beside properly outlined
    /// neighbours.</para>
    /// </summary>
    private static Control Chip(string text, ComparisonDirection direction, Palette theme)
    {
        var ink = theme.DirectionTextBrush(direction);

        return new Border
        {
            BorderBrush = ink,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(9),
            Padding = new Avalonia.Thickness(8, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ink
            }
        };
    }

    /// <summary>
    /// What the chip says. Normally the report's own wording, so the window and the copied text
    /// cannot disagree — with one exception.
    ///
    /// <para>A metric that did not move at all reads "0.0% costlier" in the report, because
    /// equality falls through to the worse branch of a two-way choice. Those bytes are frozen and
    /// stay frozen. Reprinting the word on a chip would be repeating a mistake the model has
    /// already contradicted — the direction here is Neutral — so an unchanged metric says what the
    /// report's own count rows say about an unchanged count: no change.</para>
    /// </summary>
    private static string? DeltaChipText(string? deltaLabel, ComparisonDirection direction, double? deltaPercent) =>
        deltaLabel != null && direction == ComparisonDirection.Neutral && deltaPercent == 0
            ? "no change"
            : deltaLabel;

    private static readonly FontFamily MonoFont = new("Consolas, Menlo, monospace");

    /// <summary>
    /// The design tokens, resolved once against the owning window and cached for the build.
    ///
    /// <para>Resolved from the owner rather than from the new window because the new one is not in
    /// any tree yet and would miss the application dictionary. The fallbacks match DarkTheme.axaml
    /// byte for byte and exist only so a missing key renders something sane instead of nothing —
    /// the same shape as PlanViewerControl's FindBrushResource and AdviceContentBuilder's
    /// FromTheme, which is the house pattern for a brush resolved in code.</para>
    /// </summary>
    private sealed class Palette(Window owner)
    {
        private readonly Dictionary<string, IBrush> _cache = new(StringComparer.Ordinal);

        public IBrush Brush(string key)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var brush = owner.TryFindResource(key, out var value) && value is IBrush found
                ? found
                : Fallback(key);

            _cache[key] = brush;
            return brush;
        }

        /// <summary>
        /// Green for an improvement, red for a regression, and the ordinary border colour for no
        /// verdict — never a third status colour, and never a colour for "we did not decide".
        ///
        /// <para>Edges and outlines only. BorderBrush is a line colour: at #3A3D45 on a #22252D
        /// card it is all but invisible as TEXT, which is what <see cref="DirectionTextBrush"/> is
        /// for.</para>
        /// </summary>
        public IBrush DirectionBrush(ComparisonDirection direction) => direction switch
        {
            ComparisonDirection.Better => Brush("SuccessBrush"),
            ComparisonDirection.Worse => Brush("ErrorBrush"),
            _ => Brush("BorderBrush")
        };

        /// <summary>The same three states, for anything a reader has to actually read.</summary>
        public IBrush DirectionTextBrush(ComparisonDirection direction) => direction switch
        {
            ComparisonDirection.Better => Brush("SuccessBrush"),
            ComparisonDirection.Worse => Brush("ErrorBrush"),
            _ => Brush("ForegroundMutedBrush")
        };

        private static IBrush Fallback(string key) => key switch
        {
            "BackgroundBrush" => new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23)),
            "BackgroundLightBrush" => new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2D)),
            "BorderBrush" => new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)),
            "ForegroundBrush" => new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            "ForegroundMutedBrush" => new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xC0)),
            "WarningBrush" => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)),
            "SuccessBrush" => new SolidColorBrush(Color.FromRgb(0x58, 0xB3, 0x68)),
            "ErrorBrush" => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
            _ => Brushes.White
        };
    }
}
