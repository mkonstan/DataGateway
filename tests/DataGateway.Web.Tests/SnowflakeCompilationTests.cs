using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using DataGateway.Data.Snowflake;
using SqlKata;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Proves Snowflake SQL, positional values and clone isolation without a server.</summary>
/// <remarks>Expected SQL and values are independent literals; all ordinary cases use catalog validation.</remarks>
public sealed class SnowflakeCompilationTests
{
    /// <summary>Supplies synthetic unopened connection text.</summary>
    internal const string Connection = "account=example;user=reader;password=synthetic;db=EXAMPLE;schema=PUBLIC;warehouse=COMPUTE_WH";

    /// <summary>Supplies the fifteen-binding regression request.</summary>
    internal const string ManyJson = """{"select":["mixedcase"],"filters":[{"or":[{"col":"id","op":"gte","value":2},{"and":[{"col":"label","op":"in","value":["dup","@p1?","v2","v3","v4","v5","v6","v7","v8","v9","v10"]},{"col":"flag","op":"eq","value":true}]}]},{"col":"label","op":"eq","value":"dup"},{"col":"day","op":"between","value":["2024-12-31","2025-01-01"]}],"order_by":[{"col":"id","dir":"asc"}],"limit":2}""";

    /// <summary>Builds a catalog-validated synthetic source with optional spelling probes.</summary>
    /// <param name="cap">The row cap.</param>
    /// <param name="view">The exact catalog object.</param>
    /// <param name="columns">Optional exact column names and types.</param>
    /// <returns>The successfully mapped contract.</returns>
    internal static SourceContract Source(int cap = 5, string view = "synthetic.items", (string, string)[]? columns = null)
    {
        columns ??= [("id", "number"), ("label", "text"), ("MixedCase", "text"), ("day", "date"), ("flag", "bool"), ("amount", "number")];
        var mapped = CatalogMapping.Map(new CatalogRows(
            new DataSourceRow(view, Connection) { Id = 1, Name = "synthetic_items", Title = "Items",
                Description = "Synthetic rows.", Grain = "One item.", IsActive = true, RowCap = cap, QueryTimeoutSeconds = 30 },
            columns.Select((c, i) => new DataSourceColumnRow(c.Item1, i + 1, c.Item1, "Column.", c.Item2,
                null, null, null, true, true, true, true, c.Item2 != "bool")).ToArray()));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    /// <summary>Validates JSON before handing the query to an engine.</summary>
    /// <param name="json">The request text.</param>
    /// <param name="cap">The row cap.</param>
    /// <param name="source">An optional spelling variant.</param>
    /// <returns>The accepted query.</returns>
    internal static ValidatedQuery Validate(string json, int cap = 5, SourceContract? source = null)
    {
        var result = QueryBuilding.Validate(source ?? Source(cap), json, QueryLimits.ForRowCap(cap));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    /// <summary>Compiles a catalog-validated request.</summary>
    /// <param name="json">The request text.</param>
    /// <param name="cap">The row cap.</param>
    /// <param name="source">An optional spelling variant.</param>
    /// <returns>The Snowflake compilation.</returns>
    internal static CompiledQuery Compile(string json, int cap = 5, SourceContract? source = null) =>
        new SnowflakeEngine().Compile(Validate(json, cap, source));

    /// <summary>Checks numeric insertion order, exact CLR types and unchanged values.</summary>
    /// <param name="query">The compiled request.</param>
    /// <param name="expected">Independent expected positional values.</param>
    internal static void Bindings(CompiledQuery query, params object[] expected)
    {
        Assert.Equal(Enumerable.Range(1, expected.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)), query.Parameters.Keys);
        Assert.Equal(expected.Length, query.Parameters.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = query.Parameters.ElementAt(i).Value;
            Assert.Equal(expected[i].GetType(), actual.GetType());
            Assert.Equal(expected[i], actual);
            if (actual is DateTime date) Assert.Equal(DateTimeKind.Unspecified, date.Kind);
        }
    }

    /// <summary>Checks public column spelling and types in order.</summary>
    /// <param name="query">The compilation.</param>
    /// <param name="expected">Expected name/type pairs.</param>
    internal static void Columns(CompiledQuery query, params (string, string)[] expected) =>
        Assert.Equal(expected, query.Columns.Select(c => (c.Name, c.DataType)));

    /// <summary>Writes only invariant fetch integers and leaves empty parameter collections.</summary>
    /// <remarks>The temporary culture is restored even when an assertion fails.</remarks>
    [Fact]
    public void Literal_fetch_and_empty_bindings()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var small = Compile("""{"select":["id"],"order_by":[{"col":"id","dir":"asc"}],"limit":2}""");
            Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" ORDER BY \"id\" LIMIT 3", small.Sql);
            Assert.Equal(2, small.Keep); Assert.False(small.HasMatchedRowCount); Bindings(small); Columns(small, ("id", "number"));
            var capped = Compile("""{"select":["id"],"order_by":[{"col":"id","dir":"asc"}]}""");
            Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" ORDER BY \"id\" LIMIT 6", capped.Sql);
            Assert.Equal(5, capped.Keep); Bindings(capped); Assert.DoesNotContain('?', capped.Sql);
            var larger = Compile("""{"select":["id"],"order_by":[{"col":"id","dir":"asc"}]}""", 17);
            Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" ORDER BY \"id\" LIMIT 18", larger.Sql);
            Assert.Equal(17, larger.Keep); Bindings(larger); Assert.DoesNotContain('?', larger.Sql);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    /// <summary>Quotes reserved names in all query positions.</summary>
    [Fact]
    public void Reserved_names_in_projection_filter_group_and_order()
    {
        var source = Source(view: "synthetic.table", columns: [("order", "number"), ("group", "text"), ("select", "number")]);
        var first = Compile("""{"select":["order","group","select"],"filters":[{"col":"order","op":"eq","value":2}],"order_by":[{"col":"group","dir":"asc"}]}""", source: source);
        Assert.Equal("SELECT \"order\", \"group\", \"select\" FROM \"synthetic\".\"table\" WHERE \"order\" = ? ORDER BY \"group\" LIMIT 6", first.Sql);
        Columns(first, ("order", "number"), ("group", "text"), ("select", "number"));
        var second = Compile("""{"group_by":["group"],"aggregates":[{"fn":"sum","col":"select"}],"filters":[{"col":"order","op":"eq","value":2}],"order_by":[{"col":"group","dir":"asc"}]}""", source: source);
        Assert.Equal("SELECT \"group\", SUM(\"select\") AS \"sum_select\" FROM \"synthetic\".\"table\" WHERE \"order\" = ? GROUP BY \"group\" ORDER BY \"group\" LIMIT 6", second.Sql);
        Columns(second, ("group", "text"), ("sum_select", "number"));
        foreach (var result in new[] { first, second }) { Bindings(result, 2m); Assert.Equal(5, result.Keep); Assert.False(result.HasMatchedRowCount); }
    }

    /// <summary>Preserves stored spelling after case-insensitive request lookup.</summary>
    [Fact]
    public void Exact_catalog_spelling_in_SQL_and_metadata()
    {
        var upper = Compile("""{"select":["code"],"filters":[{"col":"code","op":"eq","value":"A"}],"order_by":[{"col":"code","dir":"asc"}]}""", source: Source(view: "V", columns: [("CODE", "text")]));
        Assert.Equal("SELECT \"CODE\" FROM \"V\" WHERE \"CODE\" = ? ORDER BY \"CODE\" LIMIT 6", upper.Sql);
        Bindings(upper, "A"); Columns(upper, ("CODE", "text"));
        var lower = Compile("""{"select":["CODE"],"filters":[{"col":"CODE","op":"eq","value":"A"}],"order_by":[{"col":"CODE","dir":"asc"}]}""", source: Source(view: "V", columns: [("code", "text")]));
        Assert.Equal("SELECT \"code\" FROM \"V\" WHERE \"code\" = ? ORDER BY \"code\" LIMIT 6", lower.Sql);
        Bindings(lower, "A"); Columns(lower, ("code", "text"));
        var mixed = Compile("""{"select":["mixedcase"]}""", source: Source(view: "S1.V_2", columns: [("MixedCase", "text")]));
        Assert.Equal("SELECT \"MixedCase\" FROM \"S1\".\"V_2\" LIMIT 6", mixed.Sql);
        Bindings(mixed); Columns(mixed, ("MixedCase", "text"));
    }

    /// <summary>Adds only the private count to ungrouped aggregates.</summary>
    [Fact]
    public void Ungrouped_private_count_and_grouped_control()
    {
        var single = Compile("""{"aggregates":[{"fn":"sum","col":"amount"}]}""");
        Assert.Equal("SELECT SUM(\"amount\") AS \"sum_amount\", COUNT(*) AS \"__dg_matched_rows\" FROM \"synthetic\".\"items\" LIMIT 6", single.Sql);
        Assert.Equal(5, single.Keep); Assert.True(single.HasMatchedRowCount); Bindings(single); Columns(single, ("sum_amount", "number"));
        var grouped = Compile("""{"group_by":["label"],"aggregates":[{"fn":"sum","col":"amount"}],"order_by":[{"col":"sum_amount","dir":"desc"}]}""");
        Assert.Equal("SELECT \"label\", SUM(\"amount\") AS \"sum_amount\" FROM \"synthetic\".\"items\" GROUP BY \"label\" ORDER BY \"sum_amount\" DESC LIMIT 6", grouped.Sql);
        Assert.False(grouped.HasMatchedRowCount); Bindings(grouped); Columns(grouped, ("label", "text"), ("sum_amount", "number"));
    }

    /// <summary>Supplies independent ordered values for the nested-group compilation test and the driver binding witness.</summary>
    /// <returns>Fifteen scalar values, including duplicate text and two dates.</returns>
    internal static object[] ManyValues() => [2m, "dup", "@p1?", "v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9", "v10", "dup", new DateTime(2024, 12, 31), new DateTime(2025, 1, 1)];

    /// <summary>Retains fifteen ordered values across nested groups and marker-bearing data.</summary>
    [Fact]
    public void Fifteen_bindings_duplicates_marker_text_and_nested_groups()
    {
        var result = Compile(ManyJson);
        Assert.Equal("SELECT \"MixedCase\" FROM \"synthetic\".\"items\" WHERE ((\"id\" >= ?) OR ((\"label\" IN (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)) AND (\"flag\" = true))) AND \"label\" = ? AND \"day\" BETWEEN ? AND ? ORDER BY \"id\" LIMIT 3", result.Sql);
        Bindings(result, ManyValues()); Assert.Equal(15, result.Sql.Count(c => c == '?')); Assert.DoesNotMatch(@"@p\d+", result.Sql);
        Assert.Equal(2, result.Keep); Assert.False(result.HasMatchedRowCount); Columns(result, ("MixedCase", "text"));
    }

    /// <summary>Expands scalar membership and keeps boolean equality literal.</summary>
    /// <remarks>A repeated list member is deduplicated upstream without losing scalar CLR types.</remarks>
    [Fact]
    public void Membership_for_all_three_scalar_types_and_literal_bool_equality()
    {
        (string Col, string Op, object[] Values, string Sql)[] cases =
        [
            ("label", "in", ["AbC", "abc"], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"label\" IN (?, ?) LIMIT 6"),
            ("label", "not_in", ["AbC", "abc"], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"label\" NOT IN (?, ?) LIMIT 6"),
            ("id", "in", [2m, 3m], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"id\" IN (?, ?) LIMIT 6"),
            ("id", "not_in", [2m, 3m], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"id\" NOT IN (?, ?) LIMIT 6"),
            ("flag", "in", [true, false], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"flag\" IN (?, ?) LIMIT 6"),
            ("flag", "not_in", [true, false], "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"flag\" NOT IN (?, ?) LIMIT 6")
        ];
        foreach (var row in cases)
            foreach (var values in new[] { row.Values, row.Values.Append(row.Values[0]).ToArray() })
            {
                var result = Compile(JsonSerializer.Serialize(new { select = new[] { "id" }, filters = new[] { new { col = row.Col, op = row.Op, value = values } } }));
                Assert.Equal(row.Sql, result.Sql); Bindings(result, row.Values);
            }
        var yes = Compile("""{"select":["id"],"filters":[{"col":"flag","op":"eq","value":true}]}""");
        Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"flag\" = true LIMIT 6", yes.Sql); Bindings(yes);
        var no = Compile("""{"select":["id"],"filters":[{"col":"flag","op":"eq","value":false}]}""");
        Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"flag\" = false LIMIT 6", no.Sql); Bindings(no);
    }

    /// <summary>Pins pattern bytes at root and two nested depths.</summary>
    /// <param name="op">The public pattern operator.</param>
    /// <param name="input">The caller's pattern.</param>
    /// <param name="expected">The independently escaped value.</param>
    [Theory]
    [InlineData("like", "B%", "B%")]
    [InlineData("like", @"B%_\x", @"B%\_\\x")]
    [InlineData("contains", @"A%_\'", @"%A\%\_\\'%")]
    [InlineData("contains", @"A%_\\'", @"%A\%\_\\\\'%")]
    [InlineData("like", @"A%_[]\*'z", @"A%\_[]\\*'z")]
    [InlineData("contains", @"A%_[]\*'z", @"%A\%\_\[]\\*'z%")]
    public void Pattern_bytes_at_root_and_nested(string op, string input, string expected)
    {
        object predicate = new { col = "label", op, value = input };
        object[] filters = [predicate, new { or = new[] { predicate } }, new { or = new[] { new { and = new[] { predicate } } } }];
        string[] sql =
        [
            "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE \"label\" like ? ESCAPE '\\\\' LIMIT 6",
            "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE ((\"label\" like ? ESCAPE '\\\\')) LIMIT 6",
            "SELECT \"id\" FROM \"synthetic\".\"items\" WHERE (((\"label\" like ? ESCAPE '\\\\'))) LIMIT 6"
        ];
        for (var i = 0; i < filters.Length; i++)
        {
            var result = Compile(JsonSerializer.Serialize(new { select = new[] { "id" }, filters = new[] { filters[i] } }));
            Assert.Equal(sql[i], result.Sql); Bindings(result, expected);
            Assert.Equal(2, Regex.Match(result.Sql, "ESCAPE '([^']*)'").Groups[1].Value.Count(c => c == '\\'));
        }
    }

    /// <summary>Reuses one engine serially and concurrently without changing prepared conditions.</summary>
    /// <returns>A task completing after sixteen concurrent compiles.</returns>
    [Fact]
    public async Task Pattern_clone_independence()
    {
        var query = Validate(JsonSerializer.Serialize(new { select = new[] { "id" }, filters = new[] { new { or = new object[] {
            new { col = "label", op = "like", value = "B%" }, new { and = new object[] { new { col = "label", op = "contains", value = @"A%_\\'" }, new { col = "flag", op = "eq", value = true } } } } } }, limit = 2 }));
        var before = Conditions(query.CloneQuery()).ToArray();
        var engine = new SnowflakeEngine();
        var serial = Enumerable.Range(0, 3).Select(_ => engine.Compile(query));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => engine.Compile(query))));
        foreach (var result in serial.Concat(concurrent)) CheckCombined(result);
        Assert.Equal(before, Conditions(query.CloneQuery()));
        query.CloneQuery().Where("label", "changed").Limit(99);
        CheckCombined(engine.Compile(query));
    }

    /// <summary>Checks the independent combined-pattern golden.</summary>
    /// <param name="result">One serial or concurrent compilation.</param>
    private static void CheckCombined(CompiledQuery result)
    {
        Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" WHERE ((\"label\" like ? ESCAPE '\\\\') OR ((\"label\" like ? ESCAPE '\\\\') AND (\"flag\" = true))) LIMIT 3", result.Sql);
        Bindings(result, "B%", @"%A\%\_\\\\'%"); Columns(result, ("id", "number")); Assert.Equal(2, result.Keep); Assert.False(result.HasMatchedRowCount);
    }

    /// <summary>Snapshots string conditions recursively without compiling them.</summary>
    /// <param name="query">A detached validated clone.</param>
    /// <returns>Pattern and escape pairs in tree order.</returns>
    private static IEnumerable<(object, string)> Conditions(Query query)
    {
        foreach (var condition in query.GetComponents<AbstractCondition>("where"))
        {
            if (condition is BasicStringCondition text) yield return (text.Value, text.EscapeCharacter);
            if (condition is NestedCondition<Query> nested)
                foreach (var value in Conditions(nested.Query)) yield return value;
        }
    }

    /// <summary>Rebuilds names from ordered bindings and rejects marker drift.</summary>
    /// <remarks>Reflection stays in tests; RawSql and the original list remain untouched.</remarks>
    [Fact]
    public void Ordered_list_wins_over_named_dictionary_order()
    {
        var compiler = typeof(SnowflakeEngine).Assembly.GetType("DataGateway.Data.Snowflake.SnowflakeCompiler", true)!;
        var rewrite = compiler.GetMethod("RewriteBindings", BindingFlags.Static | BindingFlags.NonPublic)!;
        object[] values = [1m, "dup", "@p1?", "dup", 4m, 5m, 6m, 7m, 8m, 9m, 10m, 11m];
        var result = new SqlResult("?", "\\") { Sql = "SELECT @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11", RawSql = "raw witness", Bindings = values.ToList(),
            NamedBindings = Enumerable.Range(0, 12).Reverse().ToDictionary(i => "@p" + i, i => values[i]) };
        var list = result.Bindings;
        rewrite.Invoke(null, [result]);
        Assert.Equal("SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?", result.Sql);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => i.ToString(CultureInfo.InvariantCulture)), result.NamedBindings.Keys);
        Assert.Equal(values, result.NamedBindings.Values); Assert.Equal(values, result.Bindings); Assert.Same(list, result.Bindings); Assert.Equal("raw witness", result.RawSql);
        var empty = new SqlResult("?", "\\") { Sql = "SELECT 1", Bindings = [], NamedBindings = new() { ["obsolete"] = 1 } };
        rewrite.Invoke(null, [empty]); Assert.Empty(empty.NamedBindings); Assert.Equal("SELECT 1", empty.Sql);
        foreach (var sql in new[] { "SELECT 1", "SELECT @p1", "SELECT @p1suffix" })
        {
            var invalid = new SqlResult("?", "\\") { Sql = sql, Bindings = [1m] };
            var error = Assert.Throws<TargetInvocationException>(() => rewrite.Invoke(null, [invalid]));
            Assert.Equal("The compiled parameter order is invalid.", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        }
    }
}
