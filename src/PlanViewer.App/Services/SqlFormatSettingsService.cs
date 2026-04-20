using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PlanViewer.App.Services;

/// <summary>
/// Formatting options that map to <see cref="SqlScriptGeneratorOptions"/> properties.
/// Persisted as a JSON file in the app's local data directory.
/// </summary>
public sealed class SqlFormatSettings
{
    public bool AlignClauseBodies { get; set; } = true;
    public bool AlignColumnDefinitionFields { get; set; } = true;
    public bool AlignSetClauseItem { get; set; } = true;
    public bool AsKeywordOnOwnLine { get; set; } = true;
    public bool IncludeSemicolons { get; set; } = false;
    public bool IndentSetClause { get; set; } = false;
    public bool IndentViewBody { get; set; } = false;
    public int IndentationSize { get; set; } = 4;
    public string KeywordCasing { get; set; } = "Uppercase";
    public bool MultilineInsertSourcesList { get; set; } = true;
    public bool MultilineInsertTargetsList { get; set; } = true;
    public bool MultilineSelectElementsList { get; set; } = true;
    public bool MultilineSetClauseItems { get; set; } = true;
    public bool MultilineViewColumnsList { get; set; } = true;
    public bool MultilineWherePredicatesList { get; set; } = true;
    public bool NewLineBeforeCloseParenthesisInMultilineList { get; set; } = true;
    public bool NewLineBeforeFromClause { get; set; } = true;
    public bool NewLineBeforeGroupByClause { get; set; } = true;
    public bool NewLineBeforeHavingClause { get; set; } = true;
    public bool NewLineBeforeJoinClause { get; set; } = true;
    public bool NewLineBeforeOffsetClause { get; set; } = true;
    public bool NewLineBeforeOpenParenthesisInMultilineList { get; set; } = false;
    public bool NewLineBeforeOrderByClause { get; set; } = true;
    public bool NewLineBeforeOutputClause { get; set; } = true;
    public bool NewLineBeforeWhereClause { get; set; } = true;
    public bool NewLineBeforeWindowClause { get; set; } = true;
    public int SqlVersion { get; set; } = 170;

    /// <summary>
    /// Applies these settings to a <see cref="SqlScriptGeneratorOptions"/> instance.
    /// </summary>
    public SqlScriptGeneratorOptions ToGeneratorOptions()
    {
        var opts = new SqlScriptGeneratorOptions
        {
            AlignClauseBodies = AlignClauseBodies,
            AlignColumnDefinitionFields = AlignColumnDefinitionFields,
            AlignSetClauseItem = AlignSetClauseItem,
            AsKeywordOnOwnLine = AsKeywordOnOwnLine,
            IncludeSemicolons = IncludeSemicolons,
            IndentSetClause = IndentSetClause,
            IndentViewBody = IndentViewBody,
            IndentationSize = IndentationSize,
            KeywordCasing = Enum.TryParse<Microsoft.SqlServer.TransactSql.ScriptDom.KeywordCasing>(KeywordCasing, true, out var kc)
                            && Enum.IsDefined(kc)
                            ? kc : Microsoft.SqlServer.TransactSql.ScriptDom.KeywordCasing.Uppercase,
            MultilineInsertSourcesList = MultilineInsertSourcesList,
            MultilineInsertTargetsList = MultilineInsertTargetsList,
            MultilineSelectElementsList = MultilineSelectElementsList,
            MultilineSetClauseItems = MultilineSetClauseItems,
            MultilineViewColumnsList = MultilineViewColumnsList,
            MultilineWherePredicatesList = MultilineWherePredicatesList,
            NewLineBeforeCloseParenthesisInMultilineList = NewLineBeforeCloseParenthesisInMultilineList,
            NewLineBeforeFromClause = NewLineBeforeFromClause,
            NewLineBeforeGroupByClause = NewLineBeforeGroupByClause,
            NewLineBeforeHavingClause = NewLineBeforeHavingClause,
            NewLineBeforeJoinClause = NewLineBeforeJoinClause,
            NewLineBeforeOffsetClause = NewLineBeforeOffsetClause,
            NewLineBeforeOpenParenthesisInMultilineList = NewLineBeforeOpenParenthesisInMultilineList,
            NewLineBeforeOrderByClause = NewLineBeforeOrderByClause,
            NewLineBeforeOutputClause = NewLineBeforeOutputClause,
            NewLineBeforeWhereClause = NewLineBeforeWhereClause,
            NewLineBeforeWindowClause = NewLineBeforeWindowClause,
        };

        return opts;
    }
}

/// <summary>
/// Loads and saves <see cref="SqlFormatSettings"/> to a local JSON file.
/// </summary>
internal static class SqlFormatSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PerformanceStudio");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "perfstudio_format_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static SqlFormatSettings Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(SettingsPath))
                return new SqlFormatSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<SqlFormatSettings>(json, JsonOptions) ?? new SqlFormatSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SqlFormatSettings: failed to load settings: {ex.Message}");
            error = $"Could not load format settings from:\n{SettingsPath}\n\n{ex.Message}\n\nUsing defaults. Delete or fix the file to clear this error.";
            return new SqlFormatSettings();
        }
    }

    public static bool Save(SqlFormatSettings settings, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SqlFormatSettings: failed to save settings: {ex.Message}");
            error = $"Could not save format settings to:\n{SettingsPath}\n\n{ex.Message}\n\nCheck that the folder is writable and not locked by another process.";
            return false;
        }
    }
}
