using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Controls;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Services;

/// <summary>
/// Creates and shows the Advice for Humans / Robots popup window.
/// Shared between MainWindow (file mode) and QuerySessionControl (query mode).
/// </summary>
internal static class AdviceWindowHelper
{
    public static void Show(
        Window owner,
        string title,
        string content,
        AnalysisResult? analysis = null,
        PlanViewerControl? sourceViewer = null)
    {
        Action<int>? onNodeClick = sourceViewer != null
            ? nodeId => sourceViewer.NavigateToNode(nodeId)
            : null;
        var styledContent = AdviceContentBuilder.Build(content, analysis, onNodeClick);

        var scrollViewer = new ScrollViewer
        {
            Content = styledContent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var buttonTheme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!;

        var copyBtn = new Button
        {
            Content = "Copy to Clipboard",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = buttonTheme
        };

        var closeBtn = new Button
        {
            Content = "Close",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = buttonTheme
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        };
        buttonPanel.Children.Add(copyBtn);
        buttonPanel.Children.Add(closeBtn);

        // Wrap in LayoutTransformControl for Ctrl+Wheel font scaling
        var scaleTransform = new ScaleTransform(1, 1);
        var layoutTransform = new LayoutTransformControl
        {
            LayoutTransform = scaleTransform,
            Child = scrollViewer
        };

        var panel = new DockPanel { Margin = new Avalonia.Thickness(12) };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(layoutTransform);

        var window = new Window
        {
            Title = $"Performance Studio \u2014 {title}",
            Width = 700,
            Height = 600,
            MinWidth = 400,
            MinHeight = 300,
            Icon = owner.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = panel
        };

        // Ctrl+MouseWheel to increase/decrease font size
        double adviceZoom = 1.0;
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
        {
            if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                args.Handled = true;
                adviceZoom += args.Delta.Y > 0 ? 0.1 : -0.1;
                adviceZoom = Math.Max(0.5, Math.Min(3.0, adviceZoom));
                scaleTransform.ScaleX = adviceZoom;
                scaleTransform.ScaleY = adviceZoom;
            }
        }, RoutingStrategies.Tunnel);

        copyBtn.Click += async (_, _) =>
        {
            var clipboard = window.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(content);
                copyBtn.Content = "Copied!";
                await Task.Delay(1500);
                copyBtn.Content = "Copy to Clipboard";
            }
        };

        closeBtn.Click += (_, _) => window.Close();

        window.Show(owner);
    }
}
