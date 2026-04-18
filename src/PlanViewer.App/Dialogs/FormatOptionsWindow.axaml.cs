using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App.Services;

namespace PlanViewer.App.Dialogs;

public partial class FormatOptionsWindow : Window
{
    private readonly ObservableCollection<FormatOptionRow> _rows = new();
    private readonly SqlFormatSettings _defaults = new();

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
        var current = SqlFormatSettingsService.Load();
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

        SqlFormatSettingsService.Save(settings);
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

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
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
