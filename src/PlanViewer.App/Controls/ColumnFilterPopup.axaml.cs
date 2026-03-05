using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PlanViewer.App.Controls;

public partial class ColumnFilterPopup : UserControl
{
    public event EventHandler<FilterAppliedEventArgs>? FilterApplied;
    public event EventHandler? FilterCleared;

    private string _currentColumnName = "";

    private static readonly (string Display, FilterOperator Op)[] Operators =
    [
        ("Contains",           FilterOperator.Contains),
        ("Equals (=)",         FilterOperator.Equals),
        ("Not Equals (!=)",    FilterOperator.NotEquals),
        ("Starts With",        FilterOperator.StartsWith),
        ("Ends With",          FilterOperator.EndsWith),
        ("Greater Than (>)",   FilterOperator.GreaterThan),
        ("Greater or Equal (>=)", FilterOperator.GreaterThanOrEqual),
        ("Less Than (<)",      FilterOperator.LessThan),
        ("Less or Equal (<=)", FilterOperator.LessThanOrEqual),
        ("Is Empty",           FilterOperator.IsEmpty),
        ("Is Not Empty",       FilterOperator.IsNotEmpty),
    ];

    public ColumnFilterPopup()
    {
        InitializeComponent();
        foreach (var (display, _) in Operators)
            OperatorComboBox.Items.Add(display);
        OperatorComboBox.SelectedIndex = 0;
    }

    public void Initialize(string columnName, ColumnFilterState? existingFilter)
    {
        _currentColumnName = columnName;
        HeaderText.Text = $"Filter: {columnName}";

        if (existingFilter?.IsActive == true)
        {
            var idx = Array.FindIndex(Operators, o => o.Op == existingFilter.Operator);
            OperatorComboBox.SelectedIndex = idx >= 0 ? idx : 0;
            ValueTextBox.Text = existingFilter.Value;
        }
        else
        {
            OperatorComboBox.SelectedIndex = 0;
            ValueTextBox.Text = "";
        }

        UpdateValueVisibility();
        ValueTextBox.Focus();
    }

    private void UpdateValueVisibility()
    {
        var idx = OperatorComboBox.SelectedIndex;
        var op = (idx >= 0 && idx < Operators.Length) ? Operators[idx].Op : FilterOperator.Contains;
        var showValue = op != FilterOperator.IsEmpty && op != FilterOperator.IsNotEmpty;
        ValueLabel.IsVisible = showValue;
        ValueTextBox.IsVisible = showValue;
    }

    private void OperatorComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateValueVisibility();
    }

    private void ApplyFilter()
    {
        var idx = OperatorComboBox.SelectedIndex;
        if (idx < 0 || idx >= Operators.Length) return;

        FilterApplied?.Invoke(this, new FilterAppliedEventArgs
        {
            FilterState = new ColumnFilterState
            {
                ColumnName = _currentColumnName,
                Operator   = Operators[idx].Op,
                Value      = ValueTextBox.Text ?? "",
            }
        });
    }

    private void ApplyButton_Click(object? sender, RoutedEventArgs e) => ApplyFilter();

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        FilterApplied?.Invoke(this, new FilterAppliedEventArgs
        {
            FilterState = new ColumnFilterState { ColumnName = _currentColumnName }
        });
        FilterCleared?.Invoke(this, EventArgs.Empty);
    }

    private void ValueTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FilterCleared?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
