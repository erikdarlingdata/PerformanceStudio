using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class ShowPlanParser
{
    /// <summary>
    /// Strips the internal padding and hex session suffix from temp table names.
    /// SQL Server internally pads #temp names with underscores to 116 chars, then appends a hex suffix.
    /// e.g. "#comment_sil_vous_plait_______________________________0000000000A86" → "#comment_sil_vous_plait"
    /// </summary>
    private static string CleanTempTableName(string name)
    {
        if (name.Length == 0 || name[0] != '#') return name;

        // Find the end of the real name: trim trailing hex suffix, then trailing underscores
        // The hex suffix is 8-16 hex chars at the end; the padding is consecutive underscores before it
        var i = name.Length - 1;

        // Skip trailing hex digits (0-9, A-F, a-f)
        while (i > 0 && IsHexDigit(name[i])) i--;

        // Skip trailing underscores (the padding)
        while (i > 0 && name[i] == '_') i--;

        // Only clean if we actually removed a meaningful amount (at least 8 chars of padding+hex)
        if (name.Length - i > 8)
            return name[..(i + 1)];

        return name;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static IEnumerable<XElement> ScopedDescendants(XElement element, XName name)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Ns + "RelOp") continue;
            if (child.Name == name) yield return child;
            foreach (var desc in ScopedDescendants(child, name))
                yield return desc;
        }
    }

    private static string? ParseColumnList(XElement parent, string elementName)
    {
        var el = parent.Element(Ns + elementName);
        if (el == null) return null;
        var cols = el.Elements(Ns + "ColumnReference")
            .Select(c => FormatColumnRef(c))
            .Where(s => !string.IsNullOrEmpty(s));
        var result = string.Join(", ", cols);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static string FormatColumnRef(XElement colRef)
    {
        var col = colRef.Attribute("Column")?.Value ?? "";
        var tbl = colRef.Attribute("Table")?.Value ?? "";
        var result = string.IsNullOrEmpty(tbl) ? col : $"{tbl}.{col}";
        return result.Replace("[", "").Replace("]", "");
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static long ParseLong(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    }
}
