using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private void LoadSettings()
    {
        var current = SqlFormatSettingsService.Load();
        _rows.Clear();

        foreach (var prop in typeof(SqlFormatSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var currentVal = prop.GetValue(current);
            var defaultVal = prop.GetValue(_defaults);

            _rows.Add(new FormatOptionRow
            {
                Name = prop.Name,
                CurrentValue = currentVal?.ToString() ?? "",
                DefaultValue = defaultVal?.ToString() ?? "",
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
                    value = bool.Parse(row.CurrentValue);
                else if (prop.PropertyType == typeof(int))
                    value = int.Parse(row.CurrentValue);
                else
                    value = row.CurrentValue;

                prop.SetValue(settings, value);
            }
            catch
            {
                // Skip invalid values — keep default
            }
        }

        SqlFormatSettingsService.Save(settings);
    }

    private void Revert_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.CurrentValue = row.DefaultValue;
        }

        // Refresh the grid
        OptionsGrid.ItemsSource = null;
        OptionsGrid.ItemsSource = _rows;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class FormatOptionRow : INotifyPropertyChanged
{
    private string _currentValue = "";

    public string Name { get; set; } = "";

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
