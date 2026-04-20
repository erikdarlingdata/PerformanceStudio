using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PlanViewer.App.Services;

namespace PlanViewer.App.Dialogs;

public partial class FormatOptionsWindow : Window
{
    private readonly ObservableCollection<FormatOptionRow> _rows = new();
    private readonly SqlFormatSettings _defaults = new();

    private bool _isDirty;

    public FormatOptionsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    // Explicit ordering — reflection doesn't guarantee declaration order
    private static readonly string[] PropertyOrder =
    [
        "KeywordCasing", "SqlVersion", "IndentationSize",
        "AlignClauseBodies", "AlignColumnDefinitionFields", "AlignSetClauseItem",
        "AsKeywordOnOwnLine", "IncludeSemicolons",
        "IndentSetClause", "IndentViewBody",
        "MultilineInsertSourcesList", "MultilineInsertTargetsList",
        "MultilineSelectElementsList", "MultilineSetClauseItems",
        "MultilineViewColumnsList", "MultilineWherePredicatesList",
        "NewLineBeforeCloseParenthesisInMultilineList",
        "NewLineBeforeFromClause", "NewLineBeforeGroupByClause",
        "NewLineBeforeHavingClause", "NewLineBeforeJoinClause",
        "NewLineBeforeOffsetClause", "NewLineBeforeOpenParenthesisInMultilineList",
        "NewLineBeforeOrderByClause", "NewLineBeforeOutputClause",
        "NewLineBeforeWhereClause", "NewLineBeforeWindowClause",
    ];

    private static readonly Dictionary<string, string[]> ChoiceOptionsMap = new()
    {
        ["KeywordCasing"] = ["Uppercase", "Lowercase", "PascalCase"],
        ["SqlVersion"] = ["80", "90", "100", "110", "120", "130", "140", "150", "160", "170"],
    };

    private void LoadSettings()
    {
        var current = SqlFormatSettingsService.Load(out var loadError);
        if (loadError != null)
            ShowErrorPopup("Load Error", loadError);
        _rows.Clear();

        var props = typeof(SqlFormatSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        foreach (var name in PropertyOrder)
        {
            if (!props.TryGetValue(name, out var prop))
                continue;

            var currentVal = prop.GetValue(current);
            var defaultVal = prop.GetValue(_defaults);
            var isBool = prop.PropertyType == typeof(bool);

            ChoiceOptionsMap.TryGetValue(prop.Name, out var choiceOptions);

            _rows.Add(new FormatOptionRow
            {
                Name = prop.Name,
                CurrentValue = currentVal?.ToString() ?? "",
                DefaultValue = defaultVal?.ToString() ?? "",
                IsBool = isBool,
                BoolValue = isBool && currentVal is true,
                DefaultBoolValue = isBool && defaultVal is true,
                ChoiceOptions = choiceOptions,
                PropertyInfo = prop
            });
        }

        OptionsGrid.ItemsSource = _rows;

        // Track changes for dirty-state prompt
        foreach (var row in _rows)
            row.PropertyChanged += (_, _) => _isDirty = true;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var settings = new SqlFormatSettings();

        foreach (var row in _rows)
        {
            try
            {
                var prop = row.PropertyInfo;
                object? value;

                if (prop.PropertyType == typeof(bool))
                    value = row.BoolValue;
                else if (prop.PropertyType == typeof(int))
                {
                    if (!int.TryParse(row.CurrentValue, out var intVal))
                    {
                        Debug.WriteLine($"FormatOptions: invalid int value '{row.CurrentValue}' for {row.Name}, using default");
                        continue;
                    }
                    value = intVal;
                }
                else
                    value = row.CurrentValue;

                prop.SetValue(settings, value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FormatOptions: failed to set {row.Name}: {ex.Message}");
            }
        }

        if (!SqlFormatSettingsService.Save(settings, out var saveError))
        {
            ShowErrorPopup("Save Error", saveError!);
            return;
        }
        _isDirty = false;
        Close();
    }

    private void Revert_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.CurrentValue = row.DefaultValue;
            if (row.IsBool)
                row.BoolValue = row.DefaultBoolValue;
        }
    }

    private void ShowErrorPopup(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)this.FindResource("BackgroundBrush")!,
            Foreground = (IBrush)this.FindResource("ForegroundBrush")!,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13
                    }
                }
            }
        };
        dialog.ShowDialog(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        TryClose();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isDirty)
        {
            e.Cancel = true;
            TryClose();
            return;
        }
        base.OnClosing(e);
    }

    private async void TryClose()
    {
        if (!_isDirty)
        {
            _isDirty = false; // prevent re-entry
            Close();
            return;
        }

        var result = await ShowDiscardDialog();
        if (result)
        {
            _isDirty = false;
            Close();
        }
    }

    private async Task<bool> ShowDiscardDialog()
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 360,
            Height = 160,
            MinWidth = 360,
            MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)this.FindResource("BackgroundBrush")!,
            Foreground = (IBrush)this.FindResource("ForegroundBrush")!,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = "You have unsaved changes. Discard them?",
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(0, 0, 0, 16)
                    }
                }
            }
        };

        var discardBtn = new Button
        {
            Content = "Discard",
            Height = 32, Width = 90,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height = 32, Width = 90,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(discardBtn);
        buttonPanel.Children.Add(cancelBtn);

        ((StackPanel)dialog.Content!).Children.Add(buttonPanel);

        discardBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closing += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }
}

public class FormatOptionRow : INotifyPropertyChanged
{
    private string _currentValue = "";
    private bool _boolValue;

    public string Name { get; set; } = "";

    public bool IsBool { get; set; }

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            _boolValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
            // Keep CurrentValue in sync for serialization
            if (IsBool)
            {
                _currentValue = value.ToString();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
            }
        }
    }

    public bool DefaultBoolValue { get; set; }

    public string[]? ChoiceOptions { get; set; }

    public bool IsChoice => ChoiceOptions != null;

    public bool IsText => !IsBool && !IsChoice;

    public string CurrentValue
    {
        get => _currentValue;
        set
        {
            _currentValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
        }
    }

    public string DefaultValue { get; set; } = "";

    internal PropertyInfo PropertyInfo { get; set; } = null!;

    public event PropertyChangedEventHandler? PropertyChanged;
}
