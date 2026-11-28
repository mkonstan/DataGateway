# Writing a database engine

Engines compile and execute source queries. Catalog readers belong to the app; see [catalog readers](CATALOG-READERS.md). Use the [README walkthrough](README.md#add-it-to-a-new-app) for a complete composition and [configuration](CONFIGURATION.md) for aliases and limits.

## Implement the five members

`DataGateway.Data.IDatabaseEngine` has exactly five members:

| Member | Full signature |
| --- | --- |
| ProviderName | `string ProviderName { get; }` |
| Compile | `CompiledQuery Compile(ValidatedQuery query)` |
| ValidateSourceConnection | `void ValidateSourceConnection(string? connection)` |
| RecognizeFailure | `ProviderFailure? RecognizeFailure(Exception error)` |
| ReadSourceAsync | `Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)` |

`ProviderFailure(bool IsTimeout, int? Number)` carries provider timeout and error-number facts. Return null from recognition for an unrecognized exception.

Prefer a concrete class deriving from `DatabaseEngine<TConnection>` and implementing `IDatabaseEngine`, with a `ProviderName` and a `Func<string, TConnection> createConnection` passed to the protected base constructor. `TConnection` must satisfy `DbConnection, new()`. The factory supplies a fresh closed connection for each call. The five protected abstract hooks are:

| Hook | Full signature |
| --- | --- |
| CreateCompiler | `Compiler CreateCompiler()` |
| MatchedRowCountExpression | `string MatchedRowCountExpression { get; }` |
| PrepareLikePattern | `(string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern)` |
| PrepareParameter | `void PrepareParameter(DbParameter parameter, object? value)` |
| ConvertCell | `object ConvertCell(object value, OutputColumn? column)` |

Also implement public abstract `void ValidateSourceConnection(string? connection)` and `ProviderFailure? RecognizeFailure(Exception error)`.

One protected hook is optional: `virtual void PrepareParameter(DbParameter parameter, object? value, StoreType storeType)`. The base calls it instead of the two-argument hook for a value compared with a column whose catalog `store_type` is set, and its default calls the two-argument hook, so an engine that ignores store types needs no change. Store types reach that hook in two steps. Compiling records `CompiledQuery.ParameterColumns`, the column each named binding compares, when `CreateCompiler` returns a compiler that also implements `DataGateway.Data.IColumnBindingCompiler`. Then `QueryRunner.BindStoreTypesAsync` asks an engine that implements `DataGateway.Data.IStoreTypeEngine` which of the source's catalog store types it trusts (the engine parses and checks them and reports problems as `StoreTypeFinding`s) and attaches them as `CompiledQuery.StoreTypes` through `WithStoreTypes`. The SQL Server engine does both; PostgreSQL and SQLite do neither, so their sources never read the cells. An engine that wraps `SqlServerEngine` must also implement `IStoreTypeEngine`, forwarding `ResolveStoreTypesAsync` to the engine it wraps, to keep typed binding: `QueryRunner` asks only the engine it was given, so a wrapper without it binds every value as if `store_type` were empty. IN and NOT IN lists are padded on every SQL Server source, with or without store types, by the SQL Server compiler: the rows are unchanged, the SQL text and the parameter count differ (see [store types](STORE-TYPES.md#how-a-value-binds)).

## Reuse compilation and row bounds

The base clones `ValidatedQuery` through `CloneQuery()`. It fetches `RowLimit.Fetch`, which is keep+1, and uses declared output columns. It prepares provider LIKE patterns and named parameter bindings. Only `%` is a wildcard in the public vocabulary; provider escaping must preserve literal underscore and bracket behavior. The base binds CLR integers directly. The provider prepares other parameter values and converts non-null cells according to output metadata.

Ungrouped aggregates include matched-row evidence. The expression must return a nonnegative CLR long under `CompiledQuery.MatchedRowCountColumn`. The bounded consumer removes that private field and records the empty-source meaning without changing aggregate values.

`CompiledQuery` carries SQL, named parameters, Keep, HasMatchedRowCount, Columns and the init-only ParameterColumns and StoreTypes (both empty unless the engine set them). `OutputColumn` supplies Name/DataType. `QueryRows` carries detached rows, More and NoMatchingRows. These metadata must agree with the compiled result.

The public scoped `ReadSourceAsync` takes a consumer. The base's internally selected capped path supplies its own bounded consumer. Direct `IDatabaseEngine` implementations also receive a bounded `KeepAsync` consumer from QueryRunner; that path is capped too. Deriving from the base reuses compilation, bounded reading and lifecycle behavior. Direct implementations must honor compatible metadata and the supplied consumer.

## Keep resource scope awaited

Await one consumer scope with single enumeration. Return detached ordered row dictionaries. Convert database nulls to null; reject duplicate column names. Forward the command timeout and cancellation token. Invoke `opened` once after successful open and before command creation. Complete only after attempted cleanup of every acquired resource. Primary exceptions take precedence over cleanup exceptions. Saved streams and enumerators must not escape the consumer callback or start work that outlives it; materialized rows survive.

`ValidateSourceConnection` performs local syntax and required-field checks without opening. Caller-safe ArgumentException/FormatException becomes `InvalidCatalog`. Execution preserves the supplied string. `RecognizeFailure` returns timeout/number facts or null. Expected failures before successful open become source unavailability unless local connection syntax requires InvalidCatalog. Post-open timeout is `QueryTimeout`. Requested cancellation is internally `QueryCancelled`; an uncancelled OperationCanceledException is unexpected. These rules use the existing error vocabulary, including its client redaction. Provider interruption need not be immediate.

## Read the shipped implementations

| Engine | ProviderName | Status and local requirements |
| --- | --- | --- |
| [SqlServerEngine](DataGateway.Data.SqlServer/SqlServerEngine.cs) | `sqlserver` | Supported. [Source validation](DataGateway.Data.SqlServer/SqlServerEngine.Source.cs) requires a server. The example host separately requires an explicit catalog database. |
| [PostgreSqlEngine](DataGateway.Data.PostgreSql/PostgreSqlEngine.cs) | `postgresql` | Supported. Requires explicit host/database/username. |
| [SnowflakeEngine](DataGateway.Data.Snowflake/SnowflakeEngine.cs) | `snowflake` | Supported. Views are named `schema.view` or `view` in the connection's database; text comparison follows the column's collation (case-sensitive by default). See its [README](DataGateway.Data.Snowflake/README.md). |
| [SqliteEngine](DataGateway.Data.Sqlite/SqliteEngine.cs) | `sqlite` | Tests-only. Requires a file data source, ReadOnly and pooling disabled. See the [finite proof boundary](DataGateway.Data.Sqlite/README.md#proof-boundary). |

Register distinct aliases to select variants per source. No new public error-code design or runtime discovery is needed.

`QueryRunner(engine)` uses each source contract's `QueryTimeoutSeconds`, read from catalog `query_timeout_seconds` (required integer, default 30, range 1 through 86400), for its deadline, command timeout and timeout correction. Existing catalogs use `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql`. The example catalog constructors accept `(db, int catalogTimeoutSeconds = 15)` and use it only for catalog reads.
