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
}
