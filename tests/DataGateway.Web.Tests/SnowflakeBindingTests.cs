using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Snowflake;
using global::Snowflake.Data.Client;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Proves parameter preparation with the actual closed Snowflake driver.</summary>
/// <remarks>No case opens a connection or executes SQL.</remarks>
public sealed class SnowflakeBindingTests
{
    /// <summary>Binds decimal scale, Unicode, booleans and a gateway date in order.</summary>
    /// <remarks>The driver collection is also checked with all fifteen values from the nested query.</remarks>
    [Fact]
    public void Actual_driver_parameters_and_passthrough_cells()
    {
        var query = SnowflakeCompilationTests.Compile("""{"select":["id"],"filters":[{"col":"amount","op":"eq","value":1.2500},{"col":"label","op":"eq","value":"\u03bb"},{"col":"flag","op":"in","value":[true,false]},{"col":"day","op":"eq","value":"2025-01-02"}]}""");
        using var connection = new SnowflakeDbConnection();
        using var command = connection.CreateCommand();
        Bind(command, query);
        object[] expected = [1.2500m, "\u03bb", true, false, new DateTime(2025, 1, 2)];
        DbType[] types = [DbType.Decimal, DbType.String, DbType.Boolean, DbType.Boolean, DbType.Date];
        CheckParameters(command, expected, types);
        Assert.Equal(4, (decimal.GetBits(Assert.IsType<decimal>(command.Parameters[0].Value))[3] >> 16) & 0xff);
        command.Parameters.Clear();
        Bind(command, SnowflakeCompilationTests.Compile(SnowflakeCompilationTests.ManyJson));
        CheckParameters(command, SnowflakeCompilationTests.ManyValues(),
            [DbType.Decimal, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.String, DbType.Date, DbType.Date]);
        command.Parameters.Clear();
        Bind(command, new CompiledQuery("SELECT ?, ?", new Dictionary<string, object> { ["1"] = null!, ["2"] = DBNull.Value }, 1, false, []));
        Assert.Equal(2, command.Parameters.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), command.Parameters[i].ParameterName);
            Assert.Equal(DbType.String, command.Parameters[i].DbType); Assert.Same(DBNull.Value, command.Parameters[i].Value);
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
        var convert = typeof(SnowflakeEngine).GetMethod("ConvertCell", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (object Value, string Type)[] cells = [(1.2500m, "number"), ("\u03bb", "text"), (true, "bool"), (new DateTime(2025, 1, 2), "date"), (9007199254740993L, "number")];
        foreach (var cell in cells)
        {
            Assert.Same(cell.Value, convert.Invoke(new SnowflakeEngine(), [cell.Value, new OutputColumn("cell", cell.Type)]));
            Assert.Same(cell.Value, convert.Invoke(new SnowflakeEngine(), [cell.Value, null]));
        }
    }

    /// <summary>Invokes the inherited binder through a test-only reflection seam.</summary>
    /// <param name="command">The unopened driver's command.</param>
    /// <param name="query">The compiled query whose bindings are prepared.</param>
    private static void Bind(DbCommand command, CompiledQuery query) =>
        typeof(DatabaseEngine<SnowflakeDbConnection>).GetMethod("BindParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new SnowflakeEngine(), [command, query]);

    /// <summary>Checks the driver's ordered parameter names, CLR values and explicit database types.</summary>
    /// <param name="command">The prepared command.</param>
    /// <param name="expected">Independent ordered values.</param>
    /// <param name="types">Independent database types.</param>
    private static void CheckParameters(DbCommand command, object[] expected, DbType[] types)
    {
        Assert.Equal(expected.Length, command.Parameters.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var parameter = command.Parameters[i];
            Assert.IsType<SnowflakeDbParameter>(parameter);
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), parameter.ParameterName);
            Assert.Equal(types[i], parameter.DbType);
            var actual = parameter.Value;
            Assert.NotNull(actual);
            Assert.Equal(expected[i].GetType(), actual.GetType());
            Assert.Equal(expected[i], actual);
            if (actual is DateTime date) Assert.Equal(DateTimeKind.Unspecified, date.Kind);
        }
    }

    /// <summary>Checks blank text, local parser refusals and unclassified failures.</summary>
    /// <remarks>The public driver factory supplies syntax parsing; properties and server access remain unchecked.</remarks>
    [Fact]
    public void Local_connection_validation_and_failure_scope()
    {
        var engine = new SnowflakeEngine();
        foreach (var blank in new string?[] { null, "", " ", "\t\r\n" })
        {
            var error = Assert.Throws<ArgumentException>(() => engine.ValidateSourceConnection(blank));
            Assert.Equal("A source connection string is required.", error.Message); Assert.Null(error.InnerException);
        }
        engine.ValidateSourceConnection(SnowflakeCompilationTests.Connection);
        using var connection = new SnowflakeDbConnection { ConnectionString = SnowflakeCompilationTests.Connection };
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(SnowflakeCompilationTests.Connection, connection.ConnectionString);
        foreach (var malformed in new[] { "account=\"unterminated_synthetic_probe", "=synthetic_property_probe" })
        {
            var error = Assert.Throws<ArgumentException>(() => engine.ValidateSourceConnection(malformed));
            Assert.Equal("The source connection string cannot be parsed.", error.Message);
            Assert.Null(error.InnerException); Assert.DoesNotContain("synthetic_", error.ToString());
        }
        // The public factory returns the driver's parser; the closed setter itself defers parsing.
        var parser = SnowflakeDbFactory.Instance.CreateConnectionStringBuilder();
        Assert.Equal(typeof(SnowflakeDbConnection).Assembly, parser.GetType().Assembly);
        Assert.Throws<ArgumentException>(() => parser.ConnectionString = "account=\"unterminated_synthetic_probe");
        using var deferred = new SnowflakeDbConnection { ConnectionString = "account=\"unterminated_synthetic_probe" };
        Assert.Equal(ConnectionState.Closed, deferred.State);
        Assert.Throws<ArgumentNullException>(() => engine.RecognizeFailure(null!));
        foreach (var error in new Exception[] { new Exception("Synthetic failure."), new TimeoutException(), new OperationCanceledException(),
            new Exception("Synthetic wrapper.", new TimeoutException()), new AggregateException(new TimeoutException()) })
            Assert.Null(engine.RecognizeFailure(error));
    }
}
