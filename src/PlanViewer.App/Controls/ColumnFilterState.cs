using System;

namespace PlanViewer.App.Controls;

public enum FilterOperator
{
    Contains,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsEmpty,
    IsNotEmpty,
}

public class ColumnFilterState
{
    public string ColumnName { get; set; } = string.Empty;
    public FilterOperator Operator { get; set; } = FilterOperator.Contains;
    public string Value { get; set; } = string.Empty;

    public bool IsActive =>
        !string.IsNullOrEmpty(Value) ||
        Operator == FilterOperator.IsEmpty ||
        Operator == FilterOperator.IsNotEmpty;

    public string DisplayText
    {
        get
        {
            if (!IsActive) return string.Empty;

            return Operator switch
            {
                FilterOperator.Contains          => $"Contains '{Value}'",
                FilterOperator.Equals            => $"= '{Value}'",
                FilterOperator.NotEquals         => $"!= '{Value}'",
                FilterOperator.GreaterThan       => $"> {Value}",
                FilterOperator.GreaterThanOrEqual => $">= {Value}",
                FilterOperator.LessThan          => $"< {Value}",
                FilterOperator.LessThanOrEqual   => $"<= {Value}",
                FilterOperator.StartsWith        => $"Starts with '{Value}'",
                FilterOperator.EndsWith          => $"Ends with '{Value}'",
                FilterOperator.IsEmpty           => "Is Empty",
                FilterOperator.IsNotEmpty        => "Is Not Empty",
                _                                => Value,
            };
        }
    }
}

public class FilterAppliedEventArgs : EventArgs
{
    public ColumnFilterState FilterState { get; set; } = new ColumnFilterState();
}
