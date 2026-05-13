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
    private async void CopyRepro_Click(object? sender, RoutedEventArgs e)
    {
        var viewer = GetSelectedPlanViewer();
        if (viewer == null)
        {
            SetStatus("Select a plan tab first");
            return;
        }

        var planXml = viewer.RawXml;
        var queryText = viewer.QueryText ?? "";

        if (string.IsNullOrEmpty(queryText) && string.IsNullOrEmpty(planXml))
        {
            SetStatus("No query or plan data available");
            return;
        }

        /* Extract database name from plan XML StmtSimple/@DatabaseContext if available,
           otherwise fall back to the currently selected database */
        var database = ExtractDatabaseFromPlanXml(planXml) ?? _selectedDatabase;

        var reproScript = ReproScriptBuilder.BuildReproScript(
            queryText,
            database,
            planXml,
            isolationLevel: null,
            source: "Performance Studio",
            isAzureSqlDb: IsAzureConnection);

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(reproScript);
                SetStatus("Repro script copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard error: {ex.Message}");
        }
    }

    private async void Format_Click(object? sender, RoutedEventArgs e)
    {
        var sql = QueryEditor.Text;
        if (string.IsNullOrWhiteSpace(sql))
            return;

        FormatButton.IsEnabled = false;
        SetStatus("Formatting...");

        try
        {
            var settings = SqlFormatSettingsService.Load(out var loadError);
            if (loadError != null)
                SetStatus("Warning: using default format settings (load failed)");

            var (formatted, errors) = await Task.Run(() => SqlFormattingService.Format(sql, settings));

            if (errors != null && errors.Count > 0)
            {
                var errorMessages = string.Join("\n", errors.Select(err => $"Line {err.Line}: {err.Message}"));
                var dialog = new Window
                {
                    Title = "SQL Format Error",
                    Width = 500,
                    Height = 250,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Icon = GetParentWindow().Icon,
                    Background = (IBrush)this.FindResource("BackgroundBrush")!,
                    Foreground = (IBrush)this.FindResource("ForegroundBrush")!,
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(20),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Could not format: {errors.Count} parse error(s)",
                                FontWeight = Avalonia.Media.FontWeight.Bold,
                                FontSize = 14,
                                Margin = new Avalonia.Thickness(0, 0, 0, 10)
                            },
                            new TextBlock
                            {
                                Text = errorMessages,
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12
                            }
                        }
                    }
                };
                await dialog.ShowDialog(GetParentWindow());
                SetStatus($"Format failed: {errors.Count} error(s)");
                return;
            }

            var caretOffset = QueryEditor.CaretOffset;

            QueryEditor.Document.BeginUpdate();
            try
            {
                QueryEditor.Document.Replace(0, QueryEditor.Document.TextLength, formatted);
            }
            finally
            {
                QueryEditor.Document.EndUpdate();
            }

            QueryEditor.CaretOffset = Math.Min(caretOffset, QueryEditor.Document.TextLength);
            SetStatus("Formatted");
        }
        finally
        {
            FormatButton.IsEnabled = true;
        }
    }

    private void FormatOptions_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.FormatOptionsWindow();
        dialog.ShowDialog(GetParentWindow());
    }
}
