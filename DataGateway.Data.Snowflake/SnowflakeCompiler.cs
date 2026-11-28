using System.Globalization;
using System.Text.RegularExpressions;
using SqlKata;
using SqlKata.Compilers;

namespace DataGateway.Data.Snowflake;

/// <summary>Compiles validated gateway queries to Snowflake SQL and positional bindings.</summary>
/// <remarks>Inherited quoting preserves catalog spelling. The input boundary is the gateway's
/// validated query vocabulary, not arbitrary SQL.</remarks>
internal sealed class SnowflakeCompiler : PostgresCompiler
{
    /// <summary>Creates a compiler with inherited identifier quoting.</summary>
    internal SnowflakeCompiler() { }

    /// <summary>Compiles one query and names its positional parameters.</summary>
    /// <remarks>The ordered binding list, never the old named dictionary, defines value order.</remarks>
    /// <param name="query">The gateway's runnable query clone.</param>
    /// <returns>The compiled SQL and one-based positional bindings.</returns>
    public override SqlResult Compile(Query query)
    {
        var result = base.Compile(query);
        RewriteBindings(result);
        return result;
    }

    /// <summary>Replaces numbered SQL tokens and rebuilds names from ordered values.</summary>
    /// <remarks>Caller strings remain bindings and catalog identifiers cannot contain these tokens.
    /// Count and sequence checks reject compiler drift without exposing SQL or values.</remarks>
    /// <param name="result">The result returned by the base compiler.</param>
    /// <exception cref="InvalidOperationException">Tokens disagree with the ordered bindings.</exception>
    private static void RewriteBindings(SqlResult result)
    {
        var seen = 0;
        result.Sql = Regex.Replace(result.Sql, @"(?<![\w@])@p[0-9]+(?!\w)", match =>
        {
            if (match.Value != "@p" + seen.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("The compiled parameter order is invalid.");
            seen++;
            return "?";
        }, RegexOptions.CultureInvariant);
        if (seen != result.Bindings.Count)
            throw new InvalidOperationException("The compiled parameter order is invalid.");

        var named = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < result.Bindings.Count; i++)
            named.Add((i + 1).ToString(CultureInfo.InvariantCulture), result.Bindings[i]);
        result.NamedBindings = named;
    }

    /// <summary>Writes the gateway's integer fetch limit as a literal.</summary>
    /// <remarks>The base supplies keep plus one. No limit binding is created.</remarks>
    /// <param name="ctx">The query being compiled.</param>
    /// <returns>The limit clause, or empty text without a positive limit.</returns>
    public override string CompileLimit(SqlResult ctx)
    {
        var limit = ctx.Query.GetOneComponent<LimitClause>("limit", EngineCode)?.Limit ?? 0;
        return limit > 0 ? "LIMIT " + limit.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>Quotes the backslash escape as a Snowflake string literal.</summary>
    /// <remarks>Both prepared like and pre-escaped contains pass here, including nested conditions.
    /// Only the compiler's escape declaration changes; the bound pattern stays untouched.</remarks>
    /// <param name="ctx">The query being compiled.</param>
    /// <param name="condition">The string condition.</param>
    /// <returns>The condition with its escaped SQL literal.</returns>
    protected override string CompileBasicStringCondition(SqlResult ctx, BasicStringCondition condition) =>
        base.CompileBasicStringCondition(ctx, condition)
            .Replace(" ESCAPE '\\'", " ESCAPE '\\\\'", StringComparison.Ordinal);
}
