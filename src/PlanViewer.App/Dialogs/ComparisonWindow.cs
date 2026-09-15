using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
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
/// <para><b>Where the numbers come from.</b> <see cref="ComparisonFormatter.Build"/>, the same call
/// the text report renders from — so this window cannot invent a percentage the report disagrees
/// with, and Copy report hands back exactly the bytes <c>compare_plans</c> returns over MCP.
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

        var body = new StackPanel { Spacing = 10 };
        foreach (var statement in result.Statements)
            body.Children.Add(BuildStatementCard(statement, theme));

        var scroller = new ScrollViewer
        {
            Content = body,
            Padding = new Avalonia.Thickness(0, 10, 10, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var buttonTheme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!;

        var copyButton = new Button
        {
            Content = "Copy report",
            Height = 32,
            MinWidth = 110,
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
            MinWidth = 80,
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
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Children = { copyButton, closeButton }
        };

        var panel = new DockPanel { Margin = new Avalonia.Thickness(14) };
        var header = BuildHeader(result, theme);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(buttonRow);
        panel.Children.Add(scroller);

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

        copyButton.Click += async (_, _) =>
        {
            /* Rendered on demand rather than up front. Nobody has copied anything yet when the
               window opens, and the report is the one thing here that costs a string builder. */
            var report = ComparisonFormatter.Compare(planA, planB, labelA, labelB);
            copyButton.Content = await ClipboardHelper.TrySetTextAsync(window, report)
                ? "Copied!"
                : "Clipboard busy - try again";
            await Task.Delay(1500);
            copyButton.Content = "Copy report";
        };

        closeButton.Click += (_, _) => window.Close();

        window.Show(owner);
    }

    // --- Header --------------------------------------------------------------------------------

    private static Control BuildHeader(ComparisonResult result, Palette theme)
    {
        var stack = new StackPanel { Spacing = 4 };

        stack.Children.Add(new TextBlock
        {
            Text = "Plan Comparison",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.Brush("ForegroundBrush")
        });

        stack.Children.Add(PlanIdentity("A", result.LabelA, theme));
        stack.Children.Add(PlanIdentity("B", result.LabelB, theme));

        if (result.Verdict != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = result.Verdict.Text,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme.DirectionBrush(result.Verdict.Direction)
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
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = theme.Brush("WarningBrush")
            });
        }

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = theme.Brush("BorderBrush"),
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        });

        return stack;
    }

    /// <summary>
    /// "A" or "B" in a boxed letter, then the tab label it stands for — so every "Plan A" column
    /// header below has one place to resolve to.
    /// </summary>
    private static Control PlanIdentity(string letter, string label, Palette theme) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    BorderBrush = theme.Brush("BorderBrush"),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Width = 20,
                    Padding = new Avalonia.Thickness(0, 1),
                    Child = new TextBlock
                    {
                        Text = letter,
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = theme.Brush("ForegroundMutedBrush")
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
            Text = statement.StatementText,
            FontFamily = MonoFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.Brush("ForegroundMutedBrush"),
            Background = Brushes.Transparent
        });

        if (statement.Presence == StatementPresence.Both)
        {
            // Worst regression first: the reason a comparison gets opened is almost always the row
            // that got worse, and report order buries it under whatever happens to be printed first.
            stack.Children.Add(BuildRows(
                "Metric",
                ComparisonOrdering.WorstFirst(statement.Metrics),
                metric => (metric.Label, metric.DisplayA, metric.DisplayB, metric.DeltaLabel, metric.Direction),
                theme));

            if (statement.Waits.Count > 0)
            {
                stack.Children.Add(BuildRows(
                    "Wait type",
                    statement.Waits,
                    wait => (wait.WaitType, wait.DisplayA, wait.DisplayB, wait.DeltaLabel, wait.Direction),
                    theme));
            }
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
            // Left edge only: the card's whole verdict in three pixels, readable before any of the
            // numbers are.
            BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
            BorderBrush = theme.DirectionBrush(statement.NetDirection),
            Child = stack
        };
    }

    private static string NetSummary(ComparisonStatement statement) =>
        statement.NetDirection switch
        {
            ComparisonDirection.Better => $"improved on {statement.NetBasis}",
            ComparisonDirection.Worse => $"regressed on {statement.NetBasis}",
            _ => $"unchanged on {statement.NetBasis}"
        };

    // --- The four-column table both sections use -----------------------------------------------

    /// <summary>
    /// Name, Plan A, Plan B, change chip. Metrics and waits are the same shape, so they are the
    /// same table — a second copy would be free to drift in padding, alignment and colour from the
    /// one directly above it.
    /// </summary>
    private static Control BuildRows<T>(
        string nameHeader,
        IReadOnlyList<T> rows,
        Func<T, (string Name, string A, string B, string? Delta, ComparisonDirection Direction)> read,
        Palette theme)
    {
        var grid = new Grid
        {
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(110)),
                new ColumnDefinition(new GridLength(110)),
                new ColumnDefinition(GridLength.Star)
            ]
        };

        AddRow(grid, theme,
            Header(nameHeader, theme),
            Header("Plan A", theme, alignRight: true),
            Header("Plan B", theme, alignRight: true),
            Header("Change", theme));

        foreach (var row in rows)
        {
            var (name, a, b, delta, direction) = read(row);

            AddRow(grid, theme,
                new TextBlock
                {
                    Text = name,
                    FontSize = 12,
                    Margin = new Avalonia.Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = theme.Brush("ForegroundMutedBrush")
                },
                Value(a, theme),
                Value(b, theme),
                delta == null
                    ? new TextBlock { Text = "" }
                    : Chip(delta, direction, theme));
        }

        return grid;
    }

    private static void AddRow(Grid grid, Palette theme, params Control[] cells)
    {
        var index = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int column = 0; column < cells.Length; column++)
        {
            cells[column].Margin = new Avalonia.Thickness(
                cells[column].Margin.Left, index == 0 ? 0 : 3, cells[column].Margin.Right, 3);
            Grid.SetRow(cells[column], index);
            Grid.SetColumn(cells[column], column);
            grid.Children.Add(cells[column]);
        }
    }

    private static Control Header(string text, Palette theme, bool alignRight = false) =>
        new TextBlock
        {
            Text = text,
            FontSize = 10,
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
    /// </summary>
    private static Control Chip(string text, ComparisonDirection direction, Palette theme)
    {
        var edge = direction switch
        {
            ComparisonDirection.Better => theme.Brush("SuccessBrush"),
            ComparisonDirection.Worse => theme.Brush("ErrorBrush"),
            _ => theme.Brush("BorderBrush")
        };

        return new Border
        {
            BorderBrush = edge,
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
                Foreground = direction == ComparisonDirection.Neutral
                    ? theme.Brush("ForegroundMutedBrush")
                    : edge
            }
        };
    }

    private static readonly FontFamily MonoFont = new("Consolas, Menlo, monospace");

    /// <summary>
    /// The design tokens, resolved once against the owning window and cached for the build.
    ///
    /// <para>Resolved from the owner rather than from the new window because the new one is not in
    /// any tree yet and would miss the application dictionary. The fallbacks match DarkTheme.axaml
    /// byte for byte and exist only so a missing key renders something sane instead of nothing.</para>
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
        /// </summary>
        public IBrush DirectionBrush(ComparisonDirection direction) => direction switch
        {
            ComparisonDirection.Better => Brush("SuccessBrush"),
            ComparisonDirection.Worse => Brush("ErrorBrush"),
            _ => Brush("BorderBrush")
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
