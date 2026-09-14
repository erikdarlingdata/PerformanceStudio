using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PlanViewer.App.Controls;

public partial class ColumnFilterPopup : UserControl
{
    public event EventHandler<FilterAppliedEventArgs>? FilterApplied;
    public event EventHandler? FilterCleared;
    public event EventHandler<FilterAppliedEventArgs>? SearchServerRequested;

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

    /// <summary>
    /// Prepares the popup for one column. <paramref name="columnName"/> is the internal column id
    /// the filter is keyed and evaluated by; <paramref name="displayName"/> is the grid header the
    /// user actually sees, and is all that is shown in the popup.
    /// </summary>
    public void Initialize(string columnName, string displayName, ColumnFilterState? existingFilter, bool canSearchServer)
    {
        _currentColumnName = columnName;
        HeaderText.Text = $"Filter: {(string.IsNullOrEmpty(displayName) ? columnName : displayName)}";
        SearchServerButton.IsVisible = canSearchServer;

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
    }

    /// <summary>
    /// Puts the caret in the value box. Only works once the popup is open: before that this
    /// control has no visual root and Focus() is a silent no-op, which is why Initialize
    /// cannot do it — the caller focuses after opening the popup.
    /// </summary>
    internal void FocusValueBox() => ValueTextBox.Focus();

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

    private FilterAppliedEventArgs? BuildFilterArgs()
    {
        var idx = OperatorComboBox.SelectedIndex;
        if (idx < 0 || idx >= Operators.Length) return null;

        return new FilterAppliedEventArgs
        {
            FilterState = new ColumnFilterState
            {
                ColumnName = _currentColumnName,
                Operator   = Operators[idx].Op,
                Value      = ValueTextBox.Text ?? "",
            }
        };
    }

    private void ApplyFilter()
    {
        if (BuildFilterArgs() is { } args)
            FilterApplied?.Invoke(this, args);
    }

    private void ApplyButton_Click(object? sender, RoutedEventArgs e) => ApplyFilter();

    private void SearchServerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BuildFilterArgs() is { } args)
            SearchServerRequested?.Invoke(this, args);
    }

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
