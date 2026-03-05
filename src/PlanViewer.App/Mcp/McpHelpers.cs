using System;
using System.Text.Json;

namespace PlanViewer.App.Mcp;

internal static class McpHelpers
{
    public const int MaxTop = 100;

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? Truncate(string? value, int maxLength)
    {
        if (value == null || value.Length <= maxLength) return value;
        return value[..maxLength] + "... (truncated)";
    }

    public static string? ValidateTop(int top, string paramName = "top")
    {
        if (top <= 0)
            return $"Invalid {paramName} value '{top}'. Must be a positive integer (1-{MaxTop}).";
        if (top > MaxTop)
            return $"{paramName} value '{top}' exceeds maximum of {MaxTop}. Use a smaller value.";
        return null;
    }

    public static string FormatError(string operation, Exception ex) =>
        $"Error during {operation}: {ex.Message}";
}
