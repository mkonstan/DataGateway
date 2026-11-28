using System.Data;
using System.Data.Common;
using DataGateway.Core.Queries;
using global::Snowflake.Data.Client;
using SqlKata.Compilers;

namespace DataGateway.Data.Snowflake;

/// <summary>Supplies Snowflake compilation and source-connection hooks.</summary>
/// <remarks>The base owns row bounds and resource lifetime. Catalog names use stored spelling;
/// sessions must have QUOTED_IDENTIFIERS_IGNORE_CASE set to false.</remarks>
public sealed class SnowflakeEngine : DatabaseEngine<SnowflakeDbConnection>, IDatabaseEngine
{
    /// <summary>The default registration name for this engine.</summary>
    public const string ProviderName = "snowflake";

    /// <inheritdoc/>
    string IDatabaseEngine.ProviderName => ProviderName;

    /// <summary>Creates an engine whose reads receive fresh closed connections.</summary>
    public SnowflakeEngine() :
        base(text => new SnowflakeDbConnection { ConnectionString = text }) { }

    /// <summary>Creates a compiler for one query.</summary>
    /// <returns>A fresh Snowflake compiler.</returns>
    protected override Compiler CreateCompiler() => new SnowflakeCompiler();

    /// <summary>Counts matched rows under the gateway's private column name.</summary>
    protected override string MatchedRowCountExpression => "COUNT(*) AS \"__dg_matched_rows\"";

    /// <summary>Preserves percent wildcards and escapes literal backslashes and underscores.</summary>
    /// <param name="pattern">The caller's like pattern.</param>
    /// <returns>The prepared value and its backslash escape character.</returns>
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal), "\\");

    /// <summary>Binds gateway scalar values using explicit database types.</summary>
    /// <remarks>Gateway dates are DATE values; the driver's DateTime default would be a timestamp.
    /// The base binds its own CLR integer values before invoking this hook.</remarks>
    /// <param name="parameter">The new parameter, already named.</param>
    /// <param name="value">The value to bind, or null.</param>
    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                parameter.DbType = DbType.String;
                parameter.Value = DBNull.Value;
                break;
            case decimal number:
                parameter.DbType = DbType.Decimal;
                parameter.Value = number;
                break;
            case string text:
                parameter.DbType = DbType.String;
                parameter.Value = text;
                break;
            case bool flag:
                parameter.DbType = DbType.Boolean;
                parameter.Value = flag;
                break;
            case DateTime date:
                parameter.DbType = DbType.Date;
                parameter.Value = date;
                break;
            default:
                parameter.Value = value;
                break;
        }
    }

    /// <summary>Returns a non-null driver cell unchanged.</summary>
    /// <param name="value">The driver value.</param>
    /// <param name="column">The declared output column, if any.</param>
    /// <returns>The same object.</returns>
    protected override object ConvertCell(object value, OutputColumn? column) => value;

    /// <summary>Checks nonblank source text with the driver's public local parser.</summary>
    /// <remarks>The driver's factory supplies its connection-string builder for syntax checks.
    /// A closed connection receives the original text without reconstruction and is then disposed.
    /// The builder does not validate driver properties, credentials or server access.</remarks>
    /// <param name="connection">The source connection text.</param>
    /// <exception cref="ArgumentException">The text is blank or rejected locally by the driver.</exception>
    public override void ValidateSourceConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new ArgumentException("A source connection string is required.");
        try
        {
            var parser = SnowflakeDbFactory.Instance.CreateConnectionStringBuilder();
            parser.ConnectionString = connection;
            using var candidate = new SnowflakeDbConnection();
            candidate.ConnectionString = connection;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or DbException)
        {
            throw new ArgumentException("The source connection string cannot be parsed.");
        }
    }

    /// <summary>Recognizes the driver's own failures so callers see SourceUnavailable or QueryTimeout.</summary>
    /// <remarks>A role the user lacks and a view that does not exist both raise SnowflakeDbException;
    /// left unrecognized, such errors would reach callers as UnexpectedError. Snowflake reports a
    /// statement timeout as 630 and a cancelled statement as 604; the gateway's own deadline still wins first.</remarks>
    /// <param name="error">The original exception.</param>
    /// <returns>The driver failure with its error code, or null for any other exception.</returns>
    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is SnowflakeDbException snowflake
            ? new ProviderFailure(snowflake.ErrorCode is 630 or 604, snowflake.ErrorCode)
            : null;
    }
}
