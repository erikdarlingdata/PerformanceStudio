using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PlanViewer.App.Services;

/// <summary>
/// Formats T-SQL text using Microsoft.SqlServer.TransactSql.ScriptDom.
/// Inspired by https://github.com/madskristensen/SqlFormatter (MIT license).
/// </summary>
internal static class SqlFormattingService
{
    /// <summary>
    /// Formats the given T-SQL text. Returns the formatted text, or the original text
    /// with an error message if parsing fails.
    /// </summary>
    public static (string formattedText, IList<ParseError>? errors) Format(string sql, SqlFormatSettings? settings = null)
    {
        settings ??= new SqlFormatSettings();

        var parser = GetParser(settings.SqlVersion);

        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors != null && errors.Count > 0)
            return (sql, errors);

        var generator = GetGenerator(settings);
        generator.GenerateScript(fragment, out var formatted);

        return (formatted, null);
    }

    private static TSqlParser GetParser(int version)
    {
        return version switch
        {
            80 => new TSql80Parser(true),
            90 => new TSql90Parser(true),
            100 => new TSql100Parser(true),
            110 => new TSql110Parser(true),
            120 => new TSql120Parser(true),
            130 => new TSql130Parser(true),
            140 => new TSql140Parser(true),
            150 => new TSql150Parser(true),
            160 => new TSql160Parser(true),
            _ => new TSql170Parser(true),
        };
    }

    private static SqlScriptGenerator GetGenerator(SqlFormatSettings settings)
    {
        var options = settings.ToGeneratorOptions();

        return settings.SqlVersion switch
        {
            80 => new Sql80ScriptGenerator(options),
            90 => new Sql90ScriptGenerator(options),
            100 => new Sql100ScriptGenerator(options),
            110 => new Sql110ScriptGenerator(options),
            120 => new Sql120ScriptGenerator(options),
            130 => new Sql130ScriptGenerator(options),
            140 => new Sql140ScriptGenerator(options),
            150 => new Sql150ScriptGenerator(options),
            160 => new Sql160ScriptGenerator(options),
            _ => new Sql170ScriptGenerator(options),
        };
    }
}
