using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private AnalysisResult? GetCurrentAnalysis()
    {
        return GetCurrentAnalysisWithViewer().Analysis;
    }

    private void HumanAdvice_Click(object? sender, RoutedEventArgs e)
    {
        var (analysis, viewer) = GetCurrentAnalysisWithViewer();
        if (analysis == null) { SetStatus("No plan to analyze", autoClear: false); return; }

        var text = TextFormatter.Format(analysis);
        ShowAdviceWindow("Advice for Humans", text, analysis, viewer);
    }

    private void RobotAdvice_Click(object? sender, RoutedEventArgs e)
    {
        var analysis = GetCurrentAnalysis();
        if (analysis == null) { SetStatus("No plan to analyze", autoClear: false); return; }

        var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
        ShowAdviceWindow("Advice for Robots", json);
    }

    private void ShowAdviceWindow(string title, string content, AnalysisResult? analysis = null, PlanViewerControl? sourceViewer = null)
    {
        AdviceWindowHelper.Show(GetParentWindow(), title, content, analysis, sourceViewer);
    }
}
