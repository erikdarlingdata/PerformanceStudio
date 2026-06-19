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
    private void ShowMissingIndexes(List<MissingIndex> indexes)
    {
        MissingIndexContent.Children.Clear();

        if (indexes.Count > 0)
        {
            // Update expander header with count
            MissingIndexHeader.Text = $"  Missing Index Suggestions ({indexes.Count})";

            // Build each missing index row manually (no ItemsControl template binding)
            foreach (var mi in indexes)
            {
                var itemPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

                var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
                headerRow.Children.Add(new TextBlock
                {
                    Text = mi.Table,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                    FontSize = 12
                });
                headerRow.Children.Add(new TextBlock
                {
                    Text = $" \u2014 Impact: ",
                    Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                    FontSize = 12
                });
                headerRow.Children.Add(new TextBlock
                {
                    Text = $"{mi.Impact:F1}%",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB347")),
                    FontSize = 12
                });
                itemPanel.Children.Add(headerRow);

                if (!string.IsNullOrEmpty(mi.CreateStatement))
                {
                    itemPanel.Children.Add(new SelectableTextBlock
                    {
                        Text = mi.CreateStatement,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = TooltipFgBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(12, 2, 0, 0)
                    });
                }

                MissingIndexContent.Children.Add(itemPanel);
            }

            MissingIndexEmpty.IsVisible = false;
        }
        else
        {
            MissingIndexHeader.Text = "Missing Index Suggestions";
            MissingIndexEmpty.IsVisible = true;
        }
    }

}
