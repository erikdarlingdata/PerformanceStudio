using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static bool HasBatchModeNode(PlanNode node)
    {
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (string.Equals(mode, "Batch", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in node.Children)
        {
            if (HasBatchModeNode(child))
                return true;
        }
        return false;
    }

    private static void CheckForTableVariables(PlanNode node, bool isModification,
        ref bool hasTableVar, ref bool modifiesTableVar)
    {
        if (!string.IsNullOrEmpty(node.ObjectName) && node.ObjectName.StartsWith("@"))
        {
            hasTableVar = true;
            // The modification target is typically an Insert/Update/Delete operator on a table variable
            if (isModification && (node.PhysicalOp.Contains("Insert", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Merge", StringComparison.OrdinalIgnoreCase)))
            {
                modifiesTableVar = true;
            }
        }
        foreach (var child in node.Children)
            CheckForTableVariables(child, isModification, ref hasTableVar, ref modifiesTableVar);
    }

    /// <summary>
    /// Detects the NOT IN with nullable column pattern: statement has NOT IN,
    /// and a nearby Nested Loops Anti Semi Join has an IS NULL residual predicate.
    /// Checks ancestors and their children (siblings of ancestors) since the IS NULL
    /// predicate may be on a sibling Anti Semi Join rather than a direct parent.
    /// </summary>
    private static bool HasNotInPattern(PlanNode spoolNode, PlanStatement stmt)
    {
        // Check statement text for NOT IN
        if (string.IsNullOrEmpty(stmt.StatementText) ||
            !Regex.IsMatch(stmt.StatementText, @"\bNOT\s+IN\b", RegexOptions.IgnoreCase))
            return false;

        // Walk up the tree checking ancestors and their children
        var parent = spoolNode.Parent;
        while (parent != null)
        {
            if (IsAntiSemiJoinWithIsNull(parent))
                return true;

            // Check siblings: the IS NULL predicate may be on a sibling Anti Semi Join
            // (e.g. outer NL Anti Semi Join has two children: inner NL Anti Semi Join + Row Count Spool)
            foreach (var sibling in parent.Children)
            {
                if (sibling != spoolNode && IsAntiSemiJoinWithIsNull(sibling))
                    return true;
            }

            parent = parent.Parent;
        }

        return false;
    }

    private static bool IsAntiSemiJoinWithIsNull(PlanNode node) =>
        node.PhysicalOp == "Nested Loops" &&
        node.LogicalOp.Contains("Anti Semi", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(node.Predicate) &&
        node.Predicate.Contains("IS NULL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for rowstore scan operators (Index Scan, Clustered Index Scan,
    /// Table Scan). Excludes columnstore scans, spools, and constant scans.
    /// </summary>
    private static bool IsRowstoreScan(PlanNode node)
    {
        return node.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Constant", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Columnstore", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the predicate contains ONLY PROBE() bitmap filter(s)
    /// with no real residual predicate. PROBE alone is a bitmap filter pushed
    /// down from a hash join — not interesting by itself. If a real predicate
    /// exists alongside PROBE (e.g. "[col]=(1) AND PROBE(...)"), returns false.
    /// </summary>
    private static bool IsProbeOnly(string predicate)
    {
        // Strip all PROBE(...) expressions — PROBE args can contain nested parens
        var stripped = Regex.Replace(predicate, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase).Trim();

        // Remove leftover AND/OR connectors and whitespace
        stripped = Regex.Replace(stripped, @"\b(AND|OR)\b", "", RegexOptions.IgnoreCase).Trim();

        // If nothing meaningful remains, it was PROBE-only
        return stripped.Length == 0;
    }

    /// <summary>
    /// Strips PROBE(...) bitmap filter expressions from a predicate for display,
    /// leaving only the real residual predicate columns.
    /// </summary>
    private static string StripProbeExpressions(string predicate)
    {
        var stripped = Regex.Replace(predicate, @"\s*AND\s+PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)\s*AND\s+", "",
            RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    /// <summary>
    /// Returns true for any scan operator including columnstore.
    /// Excludes spools and constant scans.
    /// </summary>
    private static bool IsScanOperator(PlanNode node)
    {
        return node.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Constant", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects non-SARGable patterns in scan predicates.
    /// Returns a description of the issue, or null if the predicate is fine.
    /// </summary>
    private static string? DetectNonSargablePredicate(PlanNode node)
    {
        if (string.IsNullOrEmpty(node.Predicate))
            return null;

        // Only check rowstore scan operators — columnstore is designed to be scanned
        if (!IsRowstoreScan(node))
            return null;

        var predicate = node.Predicate;

        // CASE expression in predicate — check first because CASE bodies
        // often contain CONVERT_IMPLICIT that isn't the root cause
        if (CaseInPredicateRegex.IsMatch(predicate))
            return "CASE expression in predicate";

        // CONVERT_IMPLICIT — most common non-SARGable pattern, but only when it converts the
        // COLUMN. Converting the parameter up to the column's type costs nothing (#436).
        if (ConvertImplicitWrapsColumn(predicate))
            return "Implicit conversion (CONVERT_IMPLICIT)";

        // ISNULL / COALESCE wrapping column
        if (Regex.IsMatch(predicate, @"\b(isnull|coalesce)\s*\(", RegexOptions.IgnoreCase))
            return "ISNULL/COALESCE wrapping column";

        // Common function calls on columns — but only if the function wraps a column,
        // not a parameter/variable. Split on comparison operators to check which side
        // the function is on. Predicate format: [db].[schema].[table].[col]>func(...)
        // Every match, not just the first: a parameter-side CONVERT_IMPLICIT now falls through to
        // here, and it is skipped below. Taking only the first match would let a benign conversion
        // sitting to the left of a real function-on-column hide it (#436).
        foreach (Match funcMatch in FunctionInPredicateRegex.Matches(predicate))
        {
            var funcName = funcMatch.Groups[1].Value.ToUpperInvariant();
            if (funcName != "CONVERT_IMPLICIT" && IsFunctionOnColumnSide(predicate, funcMatch))
                return $"Function call ({funcName}) on column";
        }

        // Leading wildcard LIKE
        if (LeadingWildcardLikeRegex.IsMatch(predicate))
            return "Leading wildcard LIKE pattern";

        return null;
    }

    /// <summary>
    /// Checks whether any CONVERT_IMPLICIT in a predicate converts a COLUMN, which is the only
    /// version of it that costs a seek.
    ///
    /// <para><b>Why this is not just "contains CONVERT_IMPLICIT" (#436).</b> Data type precedence
    /// decides which side SQL Server converts, and it converts the LOWER-precedence side. Comparing a
    /// numeric(18,0) column to an int parameter converts the parameter UP:
    /// <c>[db].[dbo].[t].[col]=CONVERT_IMPLICIT(numeric(18,0),[@0],0)</c>. The column is untouched and
    /// still seekable — SQL Server will seek straight through that predicate given an index, and it
    /// raises no PlanAffectingConvert warning of its own. The damaging shape is the mirror image,
    /// <c>CONVERT_IMPLICIT(nvarchar(40),[db].[dbo].[t].[col],0)=[@d]</c>, where the conversion wraps
    /// the column and every row has to be converted before it can be compared.</para>
    ///
    /// <para>So the question is not whether a conversion is present but what is inside it, which is
    /// why this reads the CONVERT_IMPLICIT argument list rather than splitting on the comparison
    /// operator the way <see cref="IsFunctionOnColumnSide"/> does. The first argument is the target
    /// type and carries no brackets; a column reference in the remainder is the conversion input.</para>
    /// </summary>
    private static bool ConvertImplicitWrapsColumn(string predicate)
    {
        foreach (Match match in ConvertImplicitRegex.Matches(predicate))
        {
            // The regex ends at the opening paren, so its last character is where the args start.
            var arguments = ExtractBalancedArguments(predicate, match.Index + match.Length - 1);

            // Unparseable means we cannot tell what is being converted. Assume the worst, matching
            // IsFunctionOnColumnSide, rather than silently dropping a real conversion.
            if (arguments == null || ColumnReferenceRegex.IsMatch(arguments))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the text between the parenthesis at <paramref name="openParenIndex"/> and its match,
    /// or null if the parentheses do not balance. Needed because the target type of a conversion can
    /// carry its own parentheses — numeric(18,0), varchar(50) — so the first ')' is not the end.
    /// </summary>
    private static string? ExtractBalancedArguments(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return text[(openParenIndex + 1)..i];
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a function call in a predicate is on the column side of the comparison.
    /// Predicate ScalarStrings look like: [db].[schema].[table].[col]>dateadd(day,(0),[@var])
    /// If the function is only on the parameter/literal side, it's still SARGable.
    /// </summary>
    private static bool IsFunctionOnColumnSide(string predicate, Match funcMatch)
    {
        // Find the comparison operator that splits the predicate into left/right sides.
        // Operators in ScalarString: >=, <=, <>, >, <, =
        var compMatch = Regex.Match(predicate, @"(?<![<>])([<>=!]{1,2})(?![<>=])");
        if (!compMatch.Success)
            return true; // No comparison found — can't determine side, assume worst case

        var compPos = compMatch.Index;
        var funcPos = funcMatch.Index;

        // Determine which side the function is on
        var funcSide = funcPos < compPos ? "left" : "right";

        // Check if that side also contains a column reference [...].[...].[...]
        string side = funcSide == "left"
            ? predicate[..compPos]
            : predicate[(compPos + compMatch.Length)..];

        // Same column-vs-variable distinction ConvertImplicitWrapsColumn needs, so it shares the
        // one regex rather than keeping a second copy of the pattern in sync by hand.
        return ColumnReferenceRegex.IsMatch(side);
    }

    /// <summary>
    /// Verifies the OR expansion chain walking up from a Concatenation node:
    /// Nested Loops → Merge Interval → TopN Sort → [Compute Scalar] → Concatenation
    /// </summary>
    private static bool IsOrExpansionChain(PlanNode concatenationNode)
    {
        // Walk up, skipping Compute Scalar
        var parent = concatenationNode.Parent;
        while (parent != null && parent.PhysicalOp == "Compute Scalar")
            parent = parent.Parent;

        // Expect TopN Sort (XML says "TopN Sort", parser normalizes to "Top N Sort")
        if (parent == null || parent.LogicalOp != "Top N Sort")
            return false;

        // Walk up to Merge Interval
        parent = parent.Parent;
        if (parent == null || parent.PhysicalOp != "Merge Interval")
            return false;

        // Walk up to Nested Loops
        parent = parent.Parent;
        if (parent == null || parent.PhysicalOp != "Nested Loops")
            return false;

        // If this Nested Loops is inside an Anti/Semi Join, this is a NOT IN/IN
        // subquery pattern (Merge Interval optimizing range lookups), not an OR expansion
        var nlParent = parent.Parent;
        if (nlParent != null && nlParent.LogicalOp != null &&
            nlParent.LogicalOp.Contains("Semi"))
            return false;

        return true;
    }

    /// <summary>
    /// Finds Sort and Hash Match operators in the tree that consume memory.
    /// </summary>
    /// <summary>
    /// Returns true if the plan contains an adaptive join that executed as a Nested Loop.
    /// Indicates a memory grant was sized for the hash alternative but never needed.
    /// </summary>
    private static bool HasAdaptiveJoinChoseNestedLoop(PlanNode node)
    {
        if (node.IsAdaptive && node.ActualJoinType != null
            && node.ActualJoinType.Contains("Nested", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
            if (HasAdaptiveJoinChoseNestedLoop(child))
                return true;

        return false;
    }
}
