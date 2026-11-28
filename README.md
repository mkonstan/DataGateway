# DataGateway

DataGateway is a read-only library for relational data. An ASP.NET Core app supplies a catalog reader and database engines, then maps JSON and MCP endpoints.

The app owns authentication, authorization, catalog storage and logging providers. Registration alone permits open access.

## Install from NuGet

```powershell
dotnet add package DataGateway
dotnet add package DataGateway.Data.PostgreSql   # or DataGateway.Data.SqlServer, DataGateway.Data.Snowflake
```

DataGateway brings DataGateway.Core and DataGateway.Data. The engine packages DataGateway.Data.PostgreSql, DataGateway.Data.SqlServer and DataGateway.Data.Snowflake serve those databases. For any other ADO.NET provider, derive `DatabaseEngine<TConnection>` from DataGateway.Data: [ENGINES.md](https://github.com/mkonstan/DataGateway/blob/main/ENGINES.md) walks through its five members; the PostgreSQL, SQL Server, Snowflake and SQLite engines in this repository are worked examples.

## Add it to a new app

Install the .NET 10 SDK and PowerShell 7. Restore needs the pinned packages cached or an accessible package feed. Start PowerShell in the exported public source root. Use a previously unused `GatewaySample` sibling directory. The app will be outside the source tree.

SQL Server, PostgreSQL and Snowflake are supported engines. This walkthrough uses the tests-only SQLite proof adapter to avoid a database server. It does not establish production SQLite support.

```powershell
$sourceRoot = (Get-Location).Path
$appRoot = Join-Path (Split-Path $sourceRoot -Parent) 'GatewaySample'
dotnet new web --framework net10.0 --no-https --output $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Creating the app failed.' }
Set-Location $appRoot
dotnet add GatewaySample.csproj reference (Join-Path $sourceRoot 'DataGateway/DataGateway.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Adding the gateway reference failed.' }
dotnet add GatewaySample.csproj reference (Join-Path $sourceRoot 'DataGateway.Data/DataGateway.Data.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Adding the catalog API reference failed.' }
dotnet add GatewaySample.csproj reference (Join-Path $sourceRoot 'DataGateway.Data.Sqlite/DataGateway.Data.Sqlite.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Adding the proof engine reference failed.' }
```

These project references build the library, public catalog API and proof engine from source. Microsoft.Data.Sqlite and Dapper.Data 3.1.2 are transitive dependencies of those projects. This walkthrough does not depend on published DataGateway packages. No user-secret setup, database server or external seed file is needed.

Replace the generated `Program.cs` with this complete file:

<!-- sample region=Program -->
```csharp
namespace GatewaySample
{
    using Dapper.Data;
    using DataGateway;
    using DataGateway.Data.Sqlite;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Data.Sqlite;

    public static class SampleProgram
    {
        public static async Task Main(string[] args)
        {
            var file = Path.GetFullPath("sample.db");
            if (args is ["--seed"])
            {
                await SampleDatabase.CreateAsync(file, CancellationToken.None);
                return;
            }

            await using var app = CreateApplication(args, file);
            await app.RunAsync();
        }

        public static WebApplication CreateApplication(string[] args, string file)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDataGateway("sample", gateway => gateway
                .UseCatalog<SqliteConnection>(_ => new SampleCatalog(
                    DbContext<SqliteConnection>.FromFactory(
                        () => new SqliteConnection(connection)), connection))
                .AddEngine(new SqliteEngine()));
            var app = builder.Build();
            app.MapDataGateway("sample", "");
            app.MapDataGatewayApiPage("sample", "");
            return app;
        }
    }
}
```

Create `SampleCatalog.cs` with this complete file:

<!-- sample region=Catalog -->
```csharp
namespace GatewaySample
{
    using Dapper.Data;
    using DataGateway.Core.Catalog;
    using DataGateway.Data.Catalog;
    using Microsoft.Data.Sqlite;

    public sealed class SampleCatalog(
        DbContext<SqliteConnection> db, string sourceConnection)
        : CatalogService<SqliteConnection>(db)
    {
        public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new McpMetadata("DataGateway", "Read-only access to cataloged data sources.",
            [
                new("list_sources", "List sources", """No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name."""),
                new("describe_source", "Describe a source", """Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count."""),
                new("query_source", "Query a source", """Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value."""),
            ]));
        }

        public override async Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Db.BatchAsync<IReadOnlyList<SourceListRow>>(async session =>
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText =
                    "SELECT name, title, description, grain, is_active, row_cap FROM catalog_source";
                command.CommandTimeout = 30;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var rows = new List<SourceListRow>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new SourceListRow(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                        reader.GetInt32(5)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return rows.ToArray();
            }, cancellationToken);
        }

        public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
            string sourceName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sourceName);
            cancellationToken.ThrowIfCancellationRequested();
            return await Db.BatchAsync<CatalogDetail?>(async session =>
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText = """
                    SELECT name, title, description, grain, is_active, row_cap, object_name
                    FROM catalog_source WHERE name = @source COLLATE NOCASE
                    """;
                command.CommandTimeout = 30;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@source";
                parameter.Value = sourceName;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return null;
                var source = new DataSourceRow(reader.GetString(6), sourceConnection)
                {
                    Name = reader.GetString(0),
                    Title = reader.GetString(1),
                    Description = reader.GetString(2),
                    Grain = reader.GetString(3),
                    IsActive = reader.GetBoolean(4),
                    QueryTimeoutSeconds = 30,
                    RowCap = reader.GetInt32(5)
                };
                DataSourceColumnRow[] columns =
                [
                    new("id", 1, "Id", "Item identifier", "number", null, null, null,
                        true, true, true, true, true)
                ];
                cancellationToken.ThrowIfCancellationRequested();
                return new CatalogDetail("sqlite", new CatalogRows(source, columns));
            }, cancellationToken);
        }
    }
}
```

Create `SampleDatabase.cs` with this complete file:

<!-- sample region=Database -->
```csharp
namespace GatewaySample
{
    using Microsoft.Data.Sqlite;

    public static class SampleDatabase
    {
        public static async Task CreateAsync(string file, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            await using var db = new SqliteConnection(connection);
            await db.OpenAsync(cancellationToken);
            await using var transaction = db.BeginTransaction();
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE catalog_source (
                    name TEXT PRIMARY KEY COLLATE NOCASE,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    grain TEXT NOT NULL,
                    is_active INTEGER NOT NULL,
                    row_cap INTEGER NOT NULL,
                    object_name TEXT NOT NULL
                );
                INSERT INTO catalog_source VALUES (
                    'sample_items', 'Sample items', 'Three invented items',
                    'One item', 1, 2, 'items'
                );
                CREATE TABLE items (id INTEGER PRIMARY KEY);
                INSERT INTO items VALUES (1), (2), (3);
                """;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
```

This one file database contains app-chosen catalog metadata and three invented source rows. The fixed `id` column contract and injected source connection are app choices. The library requires neither this catalog schema nor SQLite storage.

```powershell
dotnet build GatewaySample.csproj
if ($LASTEXITCODE -ne 0) { throw 'Building the app failed.' }
dotnet run --project GatewaySample.csproj --no-build --no-launch-profile -- --seed
if ($LASTEXITCODE -ne 0) { throw 'Creating the sample database failed.' }
dotnet run --project GatewaySample.csproj --no-build --no-launch-profile -- --environment Production --urls http://127.0.0.1:5077
```

Seeding is once-only in the new app directory. The transaction fails on an existing schema. Seeding writes `sample.db`; gateway catalog and source connections use ReadOnly and Pooling=False. The last command stays running on loopback. In another PowerShell window:

```powershell
$list = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5077/sources/list -ContentType 'application/json' -Body '{}'
$list | ConvertTo-Json -Depth 20
$describe = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5077/sources/describe -ContentType 'application/json' -Body '{"source":"sample_items"}'
$describe | ConvertTo-Json -Depth 20
$query = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5077/query -ContentType 'application/json' -Body '{"source":"sample_items","query":{"select":["id"],"order_by":[{"col":"id","dir":"asc"}],"limit":2}}'
$query | ConvertTo-Json -Depth 20
```

List returns this complete metadata:

```text
{"sources":[{"name":"sample_items","title":"Sample items","description":"Three invented items","grain":"One item","rowCap":2}]}
```

Describe returns that same source metadata, exactly one `id` column, and `querySchema.limit.maximum` equal to 2. The column is:

```text
{"name":"id","title":"Id","description":"Item identifier","dataType":"number","unit":null,"magnitude":null,"displayFormat":null,"canSelect":true,"canFilter":true,"canSort":true,"canGroup":true,"canAggregate":true}
```

The query's complete compact answer is:

```text
{"source":"sample_items","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":1},{"id":2}],"rowCount":2,"more":true,"hint":"The result set was capped at 2 rows."}
```

PowerShell pretty-prints these answers. This app maps `/mcp`, `/scalar/` and `/openapi/v1.json` in every environment, including Production. It uses default ASP.NET Core logging providers and no sign-in policy. Ctrl+C stops it.

Read [configuration](CONFIGURATION.md), [catalog readers](CATALOG-READERS.md), [engine writing](ENGINES.md), or the [example host](DataGateway.Web/README.md) for deployment and extension details.

## Interface

Every route is `POST`, JSON in and out. The app chooses its access policy. Answers are `application/json; charset=utf-8`, with members in the order shown. Column references match without regard to case; answers use the catalog's spelling. The app chooses source lookup rules; catalog validation compares a returned source name to the requested name ignoring case.

List takes exactly `{}` (surrounding whitespace is allowed). Describe takes exactly one member, `source`. Query takes exactly `source` and `query`.

### List

<!-- example name=list kind=request route=/sources/list -->
```json
{}
```

<!-- example name=list kind=answer status=200 -->
```json
{
  "sources": [
    {
      "name": "sales_by_line",
      "title": "Sales by invoice line",
      "description": "One row per invoice line, with the customer country and the line total. Use it for revenue by country. Unlike an invoice summary, an invoice appears once per line, so sum line_total to get invoice totals.",
      "grain": "one row per invoice line",
      "rowCap": 500
    }
  ]
}
```

### Describe

<!-- example name=describe kind=request route=/sources/describe -->
```json
{
  "source": "sales_by_line"
}
```

<!-- example name=describe kind=answer status=200 -->
```json
{
  "source": {
    "name": "sales_by_line",
    "title": "Sales by invoice line",
    "description": "One row per invoice line, with the customer country and the line total. Use it for revenue by country. Unlike an invoice summary, an invoice appears once per line, so sum line_total to get invoice totals.",
    "grain": "one row per invoice line",
    "rowCap": 500
  },
  "querySchema": {
    "columns": [
      {
        "name": "country",
        "title": "Country",
        "description": "Country of the customer on the invoice.",
        "dataType": "text",
        "unit": null,
        "magnitude": null,
        "displayFormat": null,
        "canSelect": true,
        "canFilter": true,
        "canSort": true,
        "canGroup": true,
        "canAggregate": false
      },
      {
        "name": "line_total",
        "title": "Line total",
        "description": "Price times quantity for the line, in US dollars.",
        "dataType": "number",
        "unit": "USD",
        "magnitude": "none",
        "displayFormat": "0.00",
        "canSelect": true,
        "canFilter": true,
        "canSort": true,
        "canGroup": false,
        "canAggregate": true
      }
    ],
    "limit": {
      "minimum": 1,
      "maximum": 500
    }
  }
}
```

`querySchema` describes the source and how to query it: every column with its meaning and flags, then a rule for each query clause. It guides a query; DataGateway's validator decides. Its `limit.maximum` equals the source's `rowCap`, read from catalog `row_cap`. The example shows only `columns` and `limit`; the real answer also has select, filters, group_by, aggregates and order_by.

Each source's catalog `query_timeout_seconds` sets its query deadline and provider command timeout from connection open through the last kept row. This required integer defaults to 30 and accepts 1 through 86400; it is detail-only and absent from wire answers. Existing SQL Server catalogs use [catalog-query-timeout.sql](db/catalog-query-timeout.sql); PostgreSQL catalogs use [catalog-query-timeout.sql](db/postgresql/catalog-query-timeout.sql). Both migrations are idempotent and preserve audit history. Existing catalogs also run [catalog-expression.sql](db/catalog-expression.sql) or [catalog-expression.sql](db/postgresql/catalog-expression.sql), which add each column's reserved `expression` cell; DataGateway refuses a column whose expression is not null, so computed columns belong in the source's view. They then run [catalog-store-type.sql](db/catalog-store-type.sql) or [catalog-store-type.sql](db/postgresql/catalog-store-type.sql), which add each column's `store_type` and `store_type_override`: the source's own type spelling, such as `varchar(20)`, by which SQL Server sources bind filter values when the view's declared type agrees, or always when `store_type_override` is set; NULL binds them as before. [STORE-TYPES.md](STORE-TYPES.md) gives the binding rules, the visible differences, the drift check and the fill tool. Those view types are read once per process per source, on first use, so a changed view column takes effect after a restart. The host's sealed `SqlServerCatalog` and `PostgreSqlCatalog` examples accept `(db, int catalogTimeoutSeconds = 15)` for their own reads, independently of each source query deadline. Application-provided catalog readers choose their own read timeout.

### Query

<!-- example name=query kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "group_by": [
      "country"
    ],
    "aggregates": [
      {
        "fn": "sum",
        "col": "line_total"
      }
    ],
    "order_by": [
      {
        "col": "sum_line_total",
        "dir": "desc"
      }
    ],
    "limit": 5
  }
}
```

<!-- example name=query kind=answer status=200 -->
```json
{
  "source": "sales_by_line",
  "columns": [
    {
      "name": "country",
      "dataType": "text"
    },
    {
      "name": "sum_line_total",
      "dataType": "number"
    }
  ],
  "rows": [
    {
      "country": "USA",
      "sum_line_total": 523.06
    },
    {
      "country": "Canada",
      "sum_line_total": 310.50
    },
    {
      "country": "Mexico",
      "sum_line_total": 84.25
    }
  ],
  "rowCount": 3,
  "more": false,
  "hint": "Complete result set."
}
```

The query vocabulary:

- `select`: canSelect columns. Leave it out to get every selectable column. Not allowed with grouping.
- `filters`: conditions that must all hold. A condition is `{col, op, value}`. Nested `{"and":[...]}` and `{"or":[...]}` groups are allowed. Operators: eq, neq, lt, lte, gt, gte, contains, like, in, not_in, between, is_null and is_not_null; querySchema says which apply to which column. The only wildcard in a `like` pattern is `%`; `_` and `[` match themselves. The value shapes are those in the InvalidValue message in the error table below.
- `group_by`: canGroup columns.
- `aggregates`: `{fn, col}` with fn count, sum, avg, min or max. The output is named `<fn>_<col>`.
- `order_by`: `{col, dir}` with dir asc or desc. With grouping, only group_by columns or aggregate outputs, and the aggregated column must be canSort.
- `limit`: 1 up to the cap.

An answer holds at most `limit` rows, or the cap without one. `more: true` means rows were left out, with or without `limit`; `more: false` means none were. Every answer ends with `hint`: `The result set was capped at 1 row.` when `more` is true and `rowCount` is 1, or `The result set was capped at N rows.` for every other capped count (N is `rowCount`). A capped query without `order_by` appends `Rows are in no particular order.` With `more: false`, the hint is `Nothing found that matched your query.` when no source rows matched, including an ungrouped aggregate that still returns one row with 0 or null; otherwise, with an `in` filter (including one inside nested groups), it is `All matching rows returned. in values that matched no row are left out: compare the values you asked for with the rows.` If that query also has an aggregate, grouped or ungrouped, it is `All matching rows aggregated. in values that matched no row are left out and an aggregate hides them: check the in values first.` Without an `in` filter it is `Complete result set.` A `not_in` filter does not count as an `in` filter. Aggregate values are unchanged. Midnight dates come back as `yyyy-MM-dd`, ready to paste into a filter.

A request combining the vocabulary:

<!-- example name=vocabulary kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "select": [
      "country",
      "line_total"
    ],
    "filters": [
      {
        "col": "country",
        "op": "in",
        "value": [
          "USA",
          "Canada"
        ]
      },
      {
        "or": [
          {
            "col": "line_total",
            "op": "gte",
            "value": 100
          },
          {
            "col": "line_total",
            "op": "is_null"
          }
        ]
      }
    ],
    "order_by": [
      {
        "col": "line_total",
        "dir": "desc"
      }
    ],
    "limit": 10
  }
}
```

## MCP

`/mcp` uses streamable HTTP in stateless mode, with POST only. The app supplies authentication and authorization. The MCP SDK applies its HTTP transport settings to every MCP endpoint in a process, so an app that also configures the SDK's HTTP transport for an MCP server of its own is refused at startup; serve DataGateway from its own app. MCP identity comes from the gateway registration name and the DataGateway assembly version. Server display text comes from the catalog tables described below.

Some clients do not show server instructions and see only tool names and descriptions. Configure each gateway with `gateway.UseMcpTools(namePrefix: "retail", descriptionPrefix: "Retail sales and inventory.");` to identify its domain in those clients.

This names the tools `retail_list_sources`, `retail_describe_source` and `retail_query_source`. Each description starts with `Retail sales and inventory. ` followed immediately by the existing hook and text. Instructions and argument descriptions refer to the final tool names. JSON routes and failure-log operation names stay canonical. Each gateway may use different prefixes.

Both arguments are optional; null, empty and whitespace-only values unset the corresponding prefix. The last call wins, and registration validates the final settings. A name prefix must match `[a-z][a-z0-9_]{0,23}` with no trailing underscore. A description prefix must be one line, at most 80 UTF-8 bytes, and end with a period. Nonblank values are checked and preserved as supplied, without trimming; CR and LF are refused. Every final tool description must be at most 1,000 characters. Omitting the settings preserves the default names and text.

<!-- tool-table -->
| Tool | Arguments | Route |
| --- | --- | --- |
| `list_sources` | none | `POST /sources/list` |
| `describe_source` | `source` | `POST /sources/describe` |
| `query_source` | `source`, `query` | `POST /query` |

A tool's arguments are its route's request body. Its result is one text block holding exactly the route's JSON. A failure sets `isError` and uses the same code, path, message and incident ID. `UnexpectedError` adds a second text block asking the model to tell the user to report the incident ID. Unknown tools and malformed MCP messages use SDK JSON-RPC errors without a gateway failure event. A message over the body limit gets HTTP 400 `StructuralLimitExceeded` before any tool runs.

Stateless calls end with their HTTP request. Ending the connection, a client timeout, or cancelling `CallToolAsync` through `HttpClientTransport` ends that request. Cancellation is forwarded to the reader or provider; interruption need not be immediate. Internal `QueryCancelled` and `CatalogCancelled` describe source and catalog cancellation. A disconnected caller receives no error body. A separate `notifications/cancelled` POST is accepted with HTTP 202 and cannot reach another running request. The call otherwise ends by itself or at the source catalog `query_timeout_seconds` value (30 seconds by default); the deadline uses the source's catalog value; the app reader controls its catalog command timeout. Failure logging requires a failure value, so departure need not produce an event.

The [example host](DataGateway.Web/README.md#mcp) documents Windows sign-in, its local stdio proxy and client registration. Those are host choices.

## Errors

The body is `code`, `path`, `message`, `incidentId`, in that order. The message says how to fix the request. `incidentId` is 32 lowercase hex digits, the same value as on the failure's log line; report it to the DataGateway administrator.

The challenge example and its Windows-specific `NotAuthenticated` wording illustrate the example host's policy. That host sends `WWW-Authenticate: Negotiate` and no failure event for its unrecorded challenge; a refused handshake adds `incidentId`. `InternalError` is a known DataGateway-side fault; `UnexpectedError` is anything else. For both, the details are only in the log.

The example host answers an unknown path or wrong method with the framework's empty 404 or 405 after sign-in. Library registration does not require that policy.

<!-- example name=refusal kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "group_by": [
      "line_total"
    ]
  }
}
```

<!-- example name=refusal kind=answer status=400 -->
```json
{
  "code": "ColumnOperationNotAllowed",
  "path": "$.query.group_by[0]",
  "message": "The column at path cannot be grouped. Group by a column whose canGroup is true.",
  "incidentId": "9f1c2e7a4b6d4f0e8a3b5c7d9e1f2a4b"
}
```

<!-- example name=unknown-source kind=request route=/sources/describe -->
```json
{
  "source": "sales"
}
```

<!-- example name=unknown-source kind=answer status=404 -->
```json
{
  "code": "UnknownSource",
  "path": "$.source",
  "message": "Choose an active source. Available sources: sales_by_line.",
  "incidentId": "9f1c2e7a4b6d4f0e8a3b5c7d9e1f2a4b"
}
```

<!-- example name=challenge kind=answer status=401 route=/sources/list signin=none -->
```json
{
  "code": "NotAuthenticated",
  "path": "$",
  "message": "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."
}
```

<!-- error-table -->
| Code | Status | When | path |
| --- | --- | --- | --- |
| InvalidJson | 400 | Body is not JSON at `$`; query is not an object or has an unknown member at `$.query`; a repeated member or wrong JSON type at `$.query.<member>`. | `$`, `$.query`, `$.query.limit` |
| InvalidStructure | 400 | Wrong envelope members or member types, or a clause has an invalid structure. | `$`, `$.source`, `$.query.select` |
| UnknownColumn | 400 | Unknown column at its position in any clause: select[i], group_by[i], a filter's .col (keeping nested and/or segments), aggregates[i].col or order_by[i].col. | `$.query.select[0]` |
| ColumnOperationNotAllowed | 400 | A column flag refuses its operation, at the column's position in any clause, including nested filters, aggregates and sorts. | `$.query.group_by[0]` |
| InvalidOperator | 400 | Filter operator is not allowed for the column. | `$.query.filters[0].op` |
| InvalidAggregateFunction | 400 | Aggregate function is not count, sum, avg, min or max. | `$.query.aggregates[0].fn` |
| InvalidSortDirection | 400 | Sort direction is not asc or desc. | `$.query.order_by[0].dir` |
| InvalidValue | 400 | The filter value at path does not fit its operator or the column's data type. Give one value for eq, neq, lt, lte, gt, gte, contains and like; a non-empty list for in and not_in; two values, low then high, for between; and no value for is_null and is_not_null. Text takes a JSON string, number a JSON number, bool true or false, and date a string yyyy-MM-dd. | `$.query.filters[0].value` |
| InvalidClauseCombination | 400 | Explicit select cannot be combined with grouping. | `$.query.select` |
| InvalidOrderTarget | 400 | Grouped ordering must name a group column or an aggregate output. | `$.query.order_by[0].col` |
| DuplicateReference | 400 | The same column is repeated, ignoring case. | `$.query.select[1]` |
| InvalidLimit | 400 | Limit is not a positive integer. | `$.query.limit` |
| StructuralLimitExceeded | 400 | Body, query text, limit or in-list exceeds its cap. Also covers too many filter conditions (at that condition) and too deep nesting (at that group). | `$`, `$.query`, `$.query.limit`, `$.query.filters[0].value` |
| UnknownSource | 404 | Source is unknown or inactive. The message lists the active names. | `$.source` |
| NotAuthenticated | 401 | Access policy requires sign-in; the example host uses Windows sign-in. An unrecorded fallback challenge has no incidentId. | `$` |
| CatalogUnavailable | 503 | Catalog cannot be reached or read. | `$`, `$.source` |
| SourceUnavailable | 503 | Source cannot be reached or read. | `$.source` |
| QueryTimeout | 504 | A source query timed out. | `$.query` |
| InternalError | 500 | Known DataGateway-side fault. Fixed message: DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user, give the incidentId and stop; do not retry. Details only in the log. | `$` |
| UnexpectedError | 500 | Unexpected exception. Fixed message: An unexpected error stopped DataGateway from completing the request. Tell the user, give the incidentId and stop; do not retry. Details only in the log. | `$` |

Catalog invocation exceptions are sanitized to `CatalogUnavailable`, with exception type retained internally. Cancellation is `CatalogCancelled` internally. The public envelope redacts internal codes to `InternalError`; unexpected exceptions use `UnexpectedError`. A disconnected caller receives no error body. Once a response has started, failure handling aborts the connection. [Configuration](CONFIGURATION.md) describes incident and logging behavior.

## Layout

![How the parts fit: clients, a host app with its own sign-in and catalog reader, the DataGateway packages, and the databases](docs/architecture.svg)

- `DataGateway`: the reusable ASP.NET Core library, named registrations, JSON/MCP endpoints and optional API page.
- `DataGateway.Core`: pure catalog, validation and query rules with no I/O.
- `DataGateway.Data`: public app-owned catalog abstraction and query-only engine contract/base.
- `DataGateway.Data.SqlServer`, `DataGateway.Data.PostgreSql` and `DataGateway.Data.Snowflake`: supported query engines; Snowflake's catalog rules and comparison differences are in its [README](https://github.com/mkonstan/DataGateway/blob/main/DataGateway.Data.Snowflake/README.md).
- `DataGateway.Data.Sqlite`: tests-only proof; see its [boundary](DataGateway.Data.Sqlite/README.md#proof-boundary).
- `DataGateway.Web`: example host with its own catalog storage, Windows sign-in and logging providers.
- `DataGateway.Demo` and `DataGateway.Proxy`: example clients; see the host README.
- `db/`: example host schema, reader grants and seed templates; not library requirements.
- `tests/`: public suites. Node is an additional prerequisite for the complete Web suite, not the quickstart app.

From this public root, build with `dotnet build DataGateway.slnx -m:1 -nodeReuse:false` and run Core and Web test projects separately. [SqlKata.JsonQuery](https://github.com/mkonstan/SqlKata.JsonQuery) builds queries; [Dapper.Data](https://github.com/mkonstan/Dapper.Data) supplies scoped database access.

## Licence

Apache License 2.0: [LICENSE](LICENSE), [NOTICE](NOTICE).

Designed and directed by Maxim Konstantinovski. Built with AI models (Anthropic's Claude and OpenAI's GPT) under his direction and review.

## Catalog MCP metadata

MCP presentation text comes from two catalog tables: SQL Server `dbo.McpServer` and `dbo.McpTool`, or PostgreSQL `datagateway.mcpserver` and `datagateway.mcptool`.
The readers select their projection views `vMcpServer` / `vMcpTool` or `vmcpserver` / `vmcptool`, with the catalog command timeout.
The server table contains exactly one row, `id = 1`, with nonblank `title` (1-80 characters, no CR/LF) and `about` (1-1,000 characters).
The tool table contains exactly the keys `list_sources`, `describe_source`, and `query_source`; each has nonblank `title` (1-80 characters, no CR/LF) and `description` (1-1,000 characters).
Titles and text are preserved as stored, including surrounding spaces.
Lengths count UTF-16 code units, as .NET strings do, in both catalog CHECKs and runtime validation.
The seeded server title is `DataGateway` and its exact About is `Read-only access to cataloged data sources.`
The seeded tools are:

| tool_name | title | description |
| --- | --- | --- |
| `list_sources` | List sources | No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name. |
| `describe_source` | Describe a source | Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count. |
| `query_source` | Query a source | Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value. |

Fresh catalogs use `catalog-ddl.sql`, `catalog-views.sql` and `catalog-seed-example.sql` under `db/` or `db/postgresql/`.
Upgrade existing catalogs with `db/catalog-mcp-metadata.sql` or `db/postgresql/catalog-mcp-metadata.sql`; both scripts are idempotent, create the tables, views and audit triggers, seed missing rows, and preserve edited rows.
PostgreSQL readers receive grants through `db/postgresql/gateway-reader.sql`; SQL Server uses the existing reader role.
Startup validates all metadata and the final prefixed descriptions; an invalid row refuses startup with its table and field.
Every stateless POST reads metadata again through the asynchronous `ConfigureSessionOptions` hook, sharing its request catalog with any source reads.
There is no cache or server-held snapshot across initialize and tools/list; initialize and a later tools/list are separate reads.
Clients normally read the tool list when they connect, so an edit reaches a client on its next connection.
An explicit tools/list can refresh tool titles and descriptions sooner on an existing connection; text already received by the client remains unchanged.
The About precedes the fixed protocol rules with one space; the library still generates schemas and annotations.
`UseMcpTools` applies name prefixes to names and every tool reference in table text, and prepends the description prefix to each table description.
Each final description must be at most 1,000 characters, inclusive, after renaming and prefixing: 1,000 is accepted and 1,001 is refused.
Initialize and tools/list can fail with HTTP 503 CatalogUnavailable; tools/call retains its tool-result errors and existing argument paths and failure logging.
Scalar page titles and OpenAPI `info.title` always use the gateway registration name (`DataGateway.Web` in the packaged host), without reading MCP metadata; About appears in neither surface.
