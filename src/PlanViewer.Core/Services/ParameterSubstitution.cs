using System;
using System.Collections.Generic;
using System.Text;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Puts the plan's captured parameter values back into a statement's text (#467).
///
/// <para><b>Why this exists.</b> The statement text in a plan is what SQL Server compiled, not what
/// anyone typed. Under <c>PARAMETERIZATION FORCED</c> the engine rewrites literals into <c>@0</c>,
/// <c>@1</c> … before compiling, so the plan honestly records a statement nobody can run — every
/// parameter is undeclared. The same shape shows up for stored procedure parameters and for
/// <c>sp_executesql</c>. In all three cases the values are sitting in the plan's
/// <c>ParameterList</c>, which the parser already reads into
/// <see cref="PlanStatement.Parameters"/>; nothing was putting them back.</para>
///
/// <para><b>Values arrive pre-quoted.</b> A string is <c>'123456'</c> and stays that way. A number
/// is <c>(5)</c> and loses its wrapper, because <c>(5)</c> pasted into an <c>IN</c> list is not
/// what anyone means. A uniqueidentifier is <c>{guid'…'}</c>, which is showplan notation and not
/// T-SQL at all.</para>
/// </summary>
public static class ParameterSubstitution
{
    /// <summary>
    /// Rewrites <paramref name="statementText"/> with every parameter that has a value in
    /// <paramref name="parameters"/> replaced by that value.
    ///
    /// <para>Runtime values win over compiled values: the compiled value is what the plan was built
    /// for, the runtime value is what the execution being looked at actually passed, and the point
    /// of copying a statement out is to run the case in front of you.</para>
    ///
    /// <para>Parameters in the list that never appear in the text are ignored, and names in the text
    /// with no value in the list are left alone — a plan with <c>OPTION(RECOMPILE)</c> or local
    /// variables carries names without values, and inventing something there would be worse than
    /// leaving the name visible.</para>
    /// </summary>
    public static ParameterSubstitutionResult Apply(
        string? statementText,
        IReadOnlyList<PlanParameter>? parameters)
    {
        if (string.IsNullOrEmpty(statementText))
            return new ParameterSubstitutionResult(statementText ?? "", 0);

        var values = BuildValueLookup(parameters);
        if (values.Count == 0)
            return new ParameterSubstitutionResult(statementText, 0);

        /* One fact about the whole statement, settled up front: inside an EXEC statement, every
           token sitting to the left of an "=" is an assignment target — the return-status variable
           or a named argument's name — because EXEC grammar has no other use for "=" at all. A
           per-token back-scan cannot see this for the FIRST named argument (what precedes it is
           the procedure name, not a keyword), which is how "EXEC dbo.p @debug = @debug" got its
           left-hand side substituted into "EXEC dbo.p 1 = @debug". */
        var assignsThroughEquals = StatementLeadsWithExec(statementText);

        var sb = new StringBuilder(statementText.Length);
        var substitutions = 0;
        var i = 0;

        while (i < statementText.Length)
        {
            var c = statementText[i];

            /* Regions where an @name is text, not a parameter. A string literal is the case that
               matters in practice — LIKE 'kexin%' sits right next to the parameters in the #466
               repro — but a delimited identifier can hold anything, and a comment is not code. */
            if (c == '\'' || c == '"')
            {
                i = CopyDelimited(statementText, i, c, c, sb);
                continue;
            }

            if (c == '[')
            {
                i = CopyDelimited(statementText, i, '[', ']', sb);
                continue;
            }

            if (c == '-' && i + 1 < statementText.Length && statementText[i + 1] == '-')
            {
                i = CopyLineComment(statementText, i, sb);
                continue;
            }

            if (c == '/' && i + 1 < statementText.Length && statementText[i + 1] == '*')
            {
                i = CopyBlockComment(statementText, i, sb);
                continue;
            }

            /* A whole token, or nothing. "@1" inside "@11" is a different parameter, and "@0" at the
               tail of an identifier such as "t@0" is part of that identifier. The scan below claims
               the longest run of identifier characters, which handles the first; the preceding
               character is checked here, which handles the second. */
            if (c == '@' && !IsIdentifierPart(i > 0 ? statementText[i - 1] : '\0'))
            {
                var end = i + 1;
                while (end < statementText.Length && IsIdentifierPart(statementText[end]))
                    end++;

                var token = statementText[i..end];
                if (values.TryGetValue(token, out var value)
                    && !IsAssignmentTarget(statementText, i, end, assignsThroughEquals))
                {
                    sb.Append(value);
                    substitutions++;
                }
                else
                {
                    sb.Append(token);
                }

                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return substitutions == 0
            ? new ParameterSubstitutionResult(statementText, 0)
            : new ParameterSubstitutionResult(sb.ToString(), substitutions);
    }

    /// <summary>
    /// True when the parameter token spanning <paramref name="start"/> to <paramref name="end"/> is
    /// being assigned TO rather than read from, in which case its value must not be written over it.
    ///
    /// <para><b>Why (#482).</b> <c>SELECT @job_name = name, @owner_sid = owner_sid FROM …</c> is a
    /// real committed plan, and its ParameterList records both of those variables with a compiled
    /// value of <c>NULL</c>. Substituted blindly it reads <c>SELECT NULL = name, NULL = owner_sid</c>
    /// — not merely unrunnable but quietly misleading, because it now looks like a comparison. That
    /// mattered less while substitution was something you asked for on the clipboard; #482 makes it
    /// what the advice, the exports and the MCP tools show by default.</para>
    ///
    /// <para>The lead-ins recognised: <c>SELECT @a = …</c> and <c>SET @a = …</c>, with one balanced
    /// parenthesized group tolerated between the keyword and the target so <c>SELECT TOP (1) @a =
    /// …</c> is the assignment it is; the second and later items of either list, via the comma; any
    /// token in front of an <c>=</c> when the statement is an EXEC
    /// (<paramref name="assignsThroughEquals"/> — see <see cref="StatementLeadsWithExec"/>); and
    /// <c>FETCH … INTO @a, @b</c>, which assigns with no <c>=</c> anywhere. A parameter on the right
    /// of an <c>=</c> is a read and is left to be substituted, and so is one followed by <c>&gt;=</c>,
    /// <c>&lt;=</c>, <c>&lt;&gt;</c> or <c>!=</c>, since the scan forward from the token meets that
    /// operator's first character rather than the <c>=</c>.</para>
    /// </summary>
    private static bool IsAssignmentTarget(string text, int start, int end, bool assignsThroughEquals)
    {
        var forward = end;
        while (forward < text.Length && char.IsWhiteSpace(text[forward]))
            forward++;

        var followedByLoneEquals =
            forward < text.Length
            && text[forward] == '='
            && !(forward + 1 < text.Length && text[forward + 1] == '=');

        /* FETCH ... INTO @a, @b assigns without any "=": what follows the token is a comma or the
           end of the statement, so the forward scan has nothing to find and the question is
           answered entirely by what leads the list. Checked only on the no-equals path — "INTO
           @a =" is not a shape T-SQL has — which keeps every "=" case on the scans below. */
        if (!followedByLoneEquals)
            return IsFetchIntoTarget(text, start);

        /* EXEC @rc = dbo.proc, and the FIRST named argument of EXEC dbo.p @debug = @debug. The
           back-scan below cannot see either (what precedes them is a procedure name, not SELECT
           or SET), but EXEC grammar has no reads-then-"=" shape at all, so inside one the "="
           alone settles it. Later named arguments were already caught by the comma rule; this
           makes the first one match its siblings. */
        if (assignsThroughEquals)
            return true;

        var back = start - 1;
        while (back >= 0 && char.IsWhiteSpace(text[back]))
            back--;

        if (back < 0)
            return false;

        /* "SELECT @a = x, @b = y" — everything after the first comma is still a select list. */
        if (text[back] == ',')
            return true;

        /* SELECT TOP (1) @a = col: the token before the target is TOP's balanced group, which the
           old scan met as ")" and gave up on — it extracted an empty word and substituted @a into
           "NULL = col". Skip exactly ONE balanced group backwards and judge what precedes it;
           nothing but TOP puts a group in an assignment lead-in, so anything else still answers
           no. TOP (@n) rides along for free — the group's content is opaque to this walk, and @n
           itself is a read whose own forward scan meets ")" rather than "=". */
        if (text[back] == ')')
        {
            var depth = 0;
            while (back >= 0)
            {
                if (text[back] == ')')
                {
                    depth++;
                }
                else if (text[back] == '(' && --depth == 0)
                {
                    back--;
                    break;
                }

                back--;
            }

            /* Unbalanced means truncated statement text. With no way to tell what leads in, stay
               on the substitute side, which is where the empty-word scan always landed. */
            if (depth != 0)
                return false;

            while (back >= 0 && char.IsWhiteSpace(text[back]))
                back--;
        }

        var word = ReadWordEndingAt(text, ref back);

        /* TOP sits between SELECT and its group, so the decider is the word before it. */
        if (word.Equals("TOP", StringComparison.OrdinalIgnoreCase))
        {
            while (back >= 0 && char.IsWhiteSpace(text[back]))
                back--;
            word = ReadWordEndingAt(text, ref back);
        }

        return word.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || word.Equals("SET", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the token at <paramref name="start"/> sits in an INTO list — <c>FETCH … INTO @a,
    /// @b</c> — which assigns into its variables with no <c>=</c> in sight.
    ///
    /// <para>The walk hops backwards over "<c>, @name</c>" pairs so every member of the list is
    /// protected, not just the first. The hop insists each prior list item is itself an @token,
    /// which is how the commas of an IN list, a VALUES row, or a positional EXEC argument list —
    /// all places a parameter is read — fall out: at the first non-variable item, or at the "("
    /// that opens them. <c>INSERT INTO @tv</c> lands here too, and "assignment target" is the
    /// right answer there as well — a table variable's NAME is never a value to write over.</para>
    /// </summary>
    private static bool IsFetchIntoTarget(string text, int start)
    {
        var back = start - 1;

        while (true)
        {
            while (back >= 0 && char.IsWhiteSpace(text[back]))
                back--;

            if (back < 0)
                return false;

            if (text[back] == ',')
            {
                back--;
                while (back >= 0 && char.IsWhiteSpace(text[back]))
                    back--;

                var previousItem = ReadWordEndingAt(text, ref back);
                if (previousItem.Length == 0 || previousItem[0] != '@')
                    return false;

                continue;
            }

            return ReadWordEndingAt(text, ref back)
                .Equals("INTO", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Whether the statement's leading keyword chain reaches EXEC or EXECUTE, in which case an
    /// <c>=</c> can only ever mean assignment — the return-status variable or a named argument.
    ///
    /// <para>The chain is EXEC itself, or the INSERT … EXEC prelude: INSERT, an optional INTO, the
    /// target's possibly bracketed and dotted name, an optional parenthesized column list. The walk
    /// is forward from the start and gives up at the first character that cannot belong to such a
    /// prelude — an operator, a quote, a semicolon — so a SELECT or UPDATE never gets anywhere near
    /// a false positive: it hits its own <c>=</c> or <c>*</c> within a few tokens. Bracketed name
    /// parts are skipped opaquely so a procedure named [exec] cannot read as the keyword, and
    /// EXEC('…') is no counterexample — the string stays opaque to Apply's main loop, so nothing
    /// inside it is substituted regardless of what this answers. Leading comments are not chased;
    /// a statement that opens with one keeps the old per-token behavior.</para>
    /// </summary>
    private static bool StatementLeadsWithExec(string text)
    {
        var i = 0;

        /* A handful of tokens is all the INSERT ... EXEC prelude can hold. The bound is a fuse
           against pathological text, not part of the grammar. */
        for (var tokens = 0; tokens < 8; tokens++)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= text.Length)
                return false;

            var c = text[i];

            // The INSERT target's column list, skipped as one balanced unit.
            if (c == '(')
            {
                var depth = 0;
                while (i < text.Length)
                {
                    if (text[i] == '(')
                    {
                        depth++;
                    }
                    else if (text[i] == ')' && --depth == 0)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                if (depth != 0)
                    return false;

                continue;
            }

            // A delimited name part, skipped opaquely; dots just join parts.
            if (c == '[' || c == '"')
            {
                var close = text.IndexOf(c == '[' ? ']' : '"', i + 1);
                if (close < 0)
                    return false;

                i = close + 1;
                continue;
            }

            if (c == '.')
            {
                i++;
                continue;
            }

            if (!IsIdentifierPart(c))
                return false;

            var wordStart = i;
            while (i < text.Length && IsIdentifierPart(text[i]))
                i++;

            var word = text[wordStart..i];
            if (word.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
                || word.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The identifier run ending at <paramref name="back"/>, or the empty string when the character
    /// there is not part of one. Leaves <paramref name="back"/> on the character before the run.
    /// </summary>
    private static string ReadWordEndingAt(string text, ref int back)
    {
        var wordEnd = back + 1;
        while (back >= 0 && IsIdentifierPart(text[back]))
            back--;

        return text[(back + 1)..wordEnd];
    }

    /// <summary>
    /// Maps parameter name to the literal that should replace it, skipping parameters with no
    /// captured value at all.
    /// </summary>
    private static Dictionary<string, string> BuildValueLookup(IReadOnlyList<PlanParameter>? parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parameters == null)
            return values;

        foreach (var param in parameters)
        {
            if (string.IsNullOrEmpty(param.Name))
                continue;

            var raw = !string.IsNullOrEmpty(param.RuntimeValue)
                ? param.RuntimeValue
                : param.CompiledValue;

            if (string.IsNullOrEmpty(raw))
                continue;

            /* First name wins. Duplicates in a ParameterList are not expected, but silently taking
               the last one would make the result depend on document order for no reason. */
            values.TryAdd(param.Name, StripGuidWrapper(StripOuterParentheses(raw)));
        }

        return values;
    }

    /// <summary>
    /// Copies a delimited region — string literal, quoted identifier, or bracketed identifier —
    /// verbatim, and returns the index just past it. Doubled delimiters escape, so <c>'it''s'</c>
    /// is one literal. An unterminated region runs to the end of the text, which is what SQL Server
    /// would do with it and what a truncated statement text looks like.
    /// </summary>
    private static int CopyDelimited(string text, int start, char open, char close, StringBuilder sb)
    {
        sb.Append(open);
        var i = start + 1;

        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (i + 1 < text.Length && text[i + 1] == close)
                {
                    sb.Append(close).Append(close);
                    i += 2;
                    continue;
                }

                sb.Append(close);
                return i + 1;
            }

            sb.Append(text[i]);
            i++;
        }

        return i;
    }

    private static int CopyLineComment(string text, int start, StringBuilder sb)
    {
        var i = start;
        while (i < text.Length && text[i] != '\n')
        {
            sb.Append(text[i]);
            i++;
        }
        return i;
    }

    /// <summary>
    /// Copies a block comment verbatim. T-SQL nests these, so the depth is tracked rather than
    /// stopping at the first <c>*&#47;</c>.
    /// </summary>
    private static int CopyBlockComment(string text, int start, StringBuilder sb)
    {
        var depth = 0;
        var i = start;

        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                depth++;
                sb.Append("/*");
                i += 2;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
            {
                depth--;
                sb.Append("*/");
                i += 2;
                if (depth == 0)
                    return i;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return i;
    }

    /// <summary>
    /// T-SQL identifier body characters. <c>@</c> is one of them, which is why <c>@@ROWCOUNT</c>
    /// reads as a single token and never matches a parameter named <c>@ROWCOUNT</c>.
    /// </summary>
    private static bool IsIdentifierPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

    /// <summary>
    /// Unwraps a showplan-parenthesized value: <c>(5)</c> becomes <c>5</c>.
    ///
    /// <para>Only one level comes off, and only when the opening parenthesis is closed by the very
    /// last character. A value whose outer parentheses are two separate pairs is not a wrapper, and
    /// slicing the ends off it would produce text that no longer parses.</para>
    /// </summary>
    private static string StripOuterParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
            return value;

        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i == value.Length - 1 ? value[1..^1] : value;
            }
        }

        return value;
    }

    /// <summary>
    /// Unwraps showplan's uniqueidentifier notation: <c>{guid'AB12…'}</c> becomes <c>'AB12…'</c>.
    /// The braces are showplan's, not T-SQL's, and would be a syntax error if pasted.
    /// </summary>
    private static string StripGuidWrapper(string value)
    {
        if (value.StartsWith("{guid'", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("'}", StringComparison.Ordinal))
        {
            return "'" + value[6..^2] + "'";
        }

        return value;
    }
}

/// <summary>
/// The rewritten statement text and how many parameter references were actually replaced.
/// </summary>
/// <param name="Text">
/// The statement with values substituted, or the original text unchanged when nothing was replaced.
/// </param>
/// <param name="SubstitutionCount">
/// Replacements made. Zero means the statement gains nothing from being offered in substituted form,
/// which is how the UI decides whether the menu entry is worth showing.
/// </param>
public sealed record ParameterSubstitutionResult(string Text, int SubstitutionCount);
