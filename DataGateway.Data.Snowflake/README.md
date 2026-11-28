# Snowflake engine

Register a `SnowflakeEngine` through the existing `AddEngine` API. Its default provider name is `snowflake`.
The engine uses the official Snowflake.Data driver, pinned to 6.5.0.

## Catalog a Snowflake source

- Name the view as `schema.view` or `view`. The database comes from the source's connection string (`db=`); a
  three-part `database.schema.view` name is refused like any other object name with more than two parts.
- Catalog object and column names must match their stored spelling. The engine quotes identifiers without changing
  case: unquoted Snowflake names are stored in uppercase, and a quoted mixed-case view column such as `"OrderTotal"` is
  catalogued exactly as written.
- Every connection must use `QUOTED_IDENTIFIERS_IGNORE_CASE = FALSE` (Snowflake's default). The engine does not
  change session settings.
- Give the gateway's user a read-only role that can use the warehouse and select from the catalogued views, and
  nothing else. Key-pair sign-in (`authenticator=snowflake_jwt;private_key_file=<path>`) keeps passwords out of the
  catalog.

## What callers see

Snowflake's own comparison rules apply, so answers can differ from the same query on SQL Server:

- Text `eq`, `in` and `like` follow the column's collation: case-sensitive for a column without one (Snowflake's
  default), case-insensitive for a column collated `en-ci`, for example. Trailing blanks count (`'abc'` does not
  equal `'abc  '`).
- `CHAR(n)` is stored without blank padding.
- `%` is the only `like` wildcard; `_`, `\` and the `contains` input match themselves.
- `DATE` reads as a date, `TIMESTAMP_NTZ` keeps its fractional seconds, `TIMESTAMP_TZ` keeps its offset,
  `NUMBER(p,s)` keeps its scale, and `VARIANT` reads as JSON text, within what the driver's .NET types hold:
  `DateTime` and `DateTimeOffset` carry 100-nanosecond ticks (7 fractional digits, not Snowflake's 9) and `decimal`
  carries 28 to 29 significant digits (not 38); finer values follow the driver's conversion.

Driver errors (for example a role the user lacks, or a view that does not exist) are reported as `SourceUnavailable`;
Snowflake's statement-timeout and cancel codes count as timeouts, and the gateway's own deadline still applies first.

## How it compiles

SQL uses positional `?` parameters named from 1 (Snowflake rejects the `@p0` names SqlKata writes by default). The
gateway's integer fetch limit (the planned keep count plus one) is written as a literal `LIMIT`. Backslash is the
`like` escape character. Gateway dates bind with `DbType.Date`; cells pass through unchanged.

Connection validation is local: it parses the connection string and does not establish credentials or server access.
