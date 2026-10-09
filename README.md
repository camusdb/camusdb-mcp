# CamusDB MCP Server

`CamusDB.Mcp` is a standalone [Model Context Protocol](https://modelcontextprotocol.io) server
that exposes a running CamusDB cluster to MCP-capable AI clients — Claude Desktop, Claude Code,
Codex and any other MCP host. It speaks MCP over **stdio** and lets an assistant explore and drive
CamusDB through a small, typed tool surface instead of hand-rolling requests.

It depends on **only one** CamusDB package: the published
[`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client). The server talks to CamusDB
exclusively through the client's connection/command/reader API.

## Prerequisites

- A running CamusDB server reachable at a known endpoint (default `http://localhost:7141`).
- .NET 10 SDK to build and run `CamusDB.Mcp`.

## Building

```sh
dotnet build camusdb-mcp.sln
```

The output binary is `CamusDB.Mcp/bin/Debug/net10.0/CamusDB.Mcp`. You can also launch it in place
with `dotnet run --project CamusDB.Mcp`.

## Testing

```sh
dotnet test camusdb-mcp.sln
```

The unit tests always run; the integration tests need a reachable CamusDB and skip themselves
otherwise (set `CAMUS_MCP_REQUIRE_SERVER=1`, as CI does, to make an unreachable server a failure
instead of a skip). The fixture reads the same environment variables as the server, so the one
suite also runs against a server started with `CAMUSDB_AUTH_ENABLED=true`:

```sh
CAMUS_MCP_ENDPOINT=http://localhost:5095 CAMUS_MCP_DEFAULT_DATABASE=test \
CAMUS_MCP_USER=admin CAMUS_MCP_PASSWORD=… \
dotnet test camusdb-mcp.sln
```

CI runs against an unauthenticated server, so the authenticated path is a local check for now.

## Configuration (environment variables)

The server reads all configuration from environment variables. It talks to CamusDB through the
`CamusDB.Client` connection string, so you either supply a full connection string or just an
endpoint and let the server assemble one.

| Variable                        | Default                  | Description                                                                 |
| ------------------------------- | ------------------------ | --------------------------------------------------------------------------- |
| `CAMUS_MCP_CONNECTION_STRING`   | (none)                   | Full CamusDB connection string, e.g. `Endpoint=http://localhost:7141;Database=mydb;Timeout=30`. Takes precedence over `CAMUS_MCP_ENDPOINT`. |
| `CAMUS_MCP_ENDPOINT`            | `http://localhost:7141`  | CamusDB endpoint(s) when not using a full connection string. Comma-separate for round-robin. |
| `CAMUS_MCP_DEFAULT_DATABASE`    | (none)                   | Database used when a tool call omits one. Also picked up from the connection string's `Database`. |
| `CAMUS_MCP_TIMEOUT_SECONDS`     | `10` (client default)    | Per-command timeout, applied when assembling a connection string from `CAMUS_MCP_ENDPOINT`. |
| `CAMUS_MCP_MAX_ROWS`            | `1000`                   | Hard cap on rows returned by `select_query`.                                |
| `CAMUS_MCP_USER`                | (none)                   | User to authenticate as. See [Authentication](#authentication).             |
| `CAMUS_MCP_PASSWORD`            | (none)                   | That user's password. Exchanged once for a bearer token; never sent with a statement. |
| `CAMUS_MCP_ACCESS_TOKEN`        | (none)                   | A bearer token minted elsewhere, used verbatim instead of logging in.       |
| `CAMUS_MCP_TOKEN_LIFETIME_SECONDS` | `600` (client default) | Fallback token lifetime, used only when the server reports no expiry.       |

A per-request `database` argument on a tool always wins over `CAMUS_MCP_DEFAULT_DATABASE`.
Context-free statements such as `SHOW DATABASES` tolerate an empty database; table-scoped
statements require a real one.

## Authentication

CamusDB authentication is **off by default**. Against an unauthenticated server, leave the
credential variables unset — no `Authorization` header is sent and nothing changes.

Against a server started with `CAMUSDB_AUTH_ENABLED=true`, give the MCP server a user:

```json
{
  "mcpServers": {
    "camusdb": {
      "command": "/path/to/CamusDB.Mcp/bin/Debug/net10.0/CamusDB.Mcp",
      "env": {
        "CAMUS_MCP_ENDPOINT": "https://camus.internal:5095",
        "CAMUS_MCP_DEFAULT_DATABASE": "mydb",
        "CAMUS_MCP_USER": "mcp",
        "CAMUS_MCP_PASSWORD": "…"
      }
    }
  }
}
```

Credentials may equally be written into `CAMUS_MCP_CONNECTION_STRING`
(`…;User=mcp;Password=…`); the dedicated variables exist so the secret can stay out of a string
that also carries non-secret settings. When both are present the dedicated variables win, and they
also clear the connection string's aliases for the same credential (`Uid`, `UserId`, `Username`,
`Pwd`), so exactly one spelling reaches the client.

The login/token exchange is entirely `CamusDB.Client`'s: the password is traded once for a
short-lived bearer token, and every statement carries the token instead. The token is cached
**process-wide per credential set**, so the short-lived connection each tool call opens still
results in a single login — password verification is deliberately expensive server-side (PBKDF2)
and logins are rate-limited per account. Renewal and re-authentication after a revoked token are
handled by the client.

`CAMUS_MCP_ACCESS_TOKEN` passes a token obtained elsewhere and is used verbatim. The client cannot
renew it — it has no password to mint a replacement with — so an expired token surfaces as a
`CADB0516` error rather than being retried. Prefer user + password for a long-running MCP server.

### Privileges

Grant the MCP user only what the assistant should be able to do. Every table a statement touches
must be covered by a grant, or it fails with `CADB0517`:

```sql
CREATE USER mcp IDENTIFIED BY '…';
GRANT SELECT ON analytics.* TO mcp;
```

A read-only grant is the natural fit for an assistant that should explore but not modify: the
mutating tools (`create_database`, `create_table`, `insert_rows`) then fail server-side even though
they are exposed. User and grant management itself requires a superuser and is **not** exposed as a
tool — do it out of band, with a superuser connection of your own. `SHOW GRANTS` is readable
through `select_query` like any other `SHOW` statement.

### Error codes

Authentication failures are re-thrown with the CamusDB error code and a remediation hint, so a
client sees why a retry will not help:

| Code       | Meaning                                                                              |
| ---------- | ------------------------------------------------------------------------------------ |
| `CADB0516` | Missing, wrong, or expired credentials. Fix the environment variables and restart.     |
| `CADB0517` | Authenticated but lacking a privilege on a table the statement touches. Needs a `GRANT`.|
| `CADB0518` | Too many login attempts for that account (server-side rate limit). Wait.                |
| `CADB0519` | Credentials sent over plaintext where the server requires TLS. Use an `https://` endpoint.|

### TLS

With authentication enabled the server refuses credential-bearing requests over plaintext, except
from loopback — so local development over `http://localhost` works without certificates. For any
non-loopback deployment use an `https://` endpoint, or the server answers `CADB0519`. When TLS
terminates in front of the node (ingress, sidecar, mesh), start the server with
`--require-tls-when-auth-enabled false` and keep the plaintext hop inside the trust boundary.

## Launching from an MCP client

You do not run the server by hand — the MCP client launches it over stdio.

### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "camusdb": {
      "command": "/path/to/CamusDB.Mcp/bin/Debug/net10.0/CamusDB.Mcp",
      "env": {
        "CAMUS_MCP_ENDPOINT": "http://localhost:7141",
        "CAMUS_MCP_DEFAULT_DATABASE": "mydb"
      }
    }
  }
}
```

### Claude Code (`.mcp.json`)

Using the built binary:

```json
{
  "mcpServers": {
    "camusdb": {
      "command": "/path/to/CamusDB.Mcp/bin/Debug/net10.0/CamusDB.Mcp",
      "env": {
        "CAMUS_MCP_CONNECTION_STRING": "Endpoint=http://localhost:7141;Database=mydb;Timeout=30"
      }
    }
  }
}
```

Or launching via the SDK without pre-building:

```json
{
  "mcpServers": {
    "camusdb": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/camusdb-mcp/CamusDB.Mcp"],
      "env": {
        "CAMUS_MCP_ENDPOINT": "http://localhost:7141",
        "CAMUS_MCP_DEFAULT_DATABASE": "mydb"
      }
    }
  }
}
```

## Tools

### Read tools (never mutate)

| Tool               | Description                                                                                 |
| ------------------ | ------------------------------------------------------------------------------------------- |
| `list_databases`   | List all databases in the CamusDB cluster.                                                  |
| `list_tables`      | List all tables in a database.                                                              |
| `list_views`       | List the views and materialized views in a database (materialized views with `populated` and `refreshed_at`). |
| `list_sequences`   | List the sequences in a database with their options (via `SHOW SEQUENCES`).                |
| `list_branches`    | List all branches of a root database (via `SHOW BRANCHES FROM`). Returns an empty list if none exist. |
| `list_ancestors`   | List the ancestors of a branch database (via `SHOW ANCESTORS FROM`). Empty for a root database. |
| `list_indexes`     | List all readable indexes on a table (via `SHOW INDEXES FROM`). Mid-backfill indexes omitted.|
| `get_table_schema` | Get column schema for a table (via `SHOW COLUMNS FROM`): names, types, nullability, defaults.|
| `show_create`      | Get the DDL that recreates a table, view, materialized view or sequence (via `SHOW CREATE …`). Tables can omit secondary indexes. |
| `get_table_statistics` | Get the optimizer statistics for a table (via `SHOW STATISTICS FOR TABLE`).             |

Every other `SHOW` statement (`SHOW GRANTS`, `SHOW USERS`, `SHOW VARIABLES`, `SHOW CLUSTER SETTINGS`,
`SHOW ENGINE STATS`, `SHOW SLOW QUERIES`, `SHOW RANGES`, `SHOW ORPHAN TABLES`, `SHOW DATABASE`, the
`LIKE` filters, …) is available through `select_query`. Result rows pass through unchanged, so a
column the server adds to a `SHOW` result reaches the client without a change here.

### Query tools (read-only, enforced by the statement guard)

| Tool            | Description                                                                                 |
| --------------- | ------------------------------------------------------------------------------------------- |
| `select_query`  | Execute a SELECT, SHOW or DESCRIBE statement. Mutating SQL is rejected before execution. Capped at `max_rows`. |
| `explain_query` | Execute an EXPLAIN statement (plan / logical / physical / analyze). Bare SELECT is prefixed with EXPLAIN. |

#### Parameters and result values

Parameters (`@name` placeholders) are JSON values:

- A string, boolean or `null` binds as that type.
- An integer that fits in 64 bits binds as `int64`.
- Any other number binds as `float64` when it has at most 15 significant digits. Otherwise it binds
  as an exact `NUMERIC` with its JSON text, so no digit is lost. The server converts the value to
  the target column type on write.
- An array of scalars binds as an array parameter (for example `WHERE id = ANY(@ids)`, or a value for
  an `array(T)` column). All the elements must be of one JSON kind.

In results, a `NUMERIC` value is returned as its exact decimal text (`"1.5"`, `"1200"`). A .NET
`decimal` holds only 28–29 significant digits, but a `NUMERIC` holds 38.

### Mutating tools

| Tool              | Description                                                                                |
| ----------------- | ------------------------------------------------------------------------------------------ |
| `create_database` | Create a new database (supports IF NOT EXISTS) via the client's CREATE DATABASE entry point.|
| `create_table`    | Create a table from a typed column definition list. Identifiers and types are validated.   |
| `insert_rows`     | Insert one or more rows. Values are passed as parameters (no string interpolation), chunked at 500 rows/batch. |

## Read-only vs. mutating boundary

`select_query` and `explain_query` classify the SQL **locally** before any statement reaches the
server. The classifier (`StatementGuard`) is self-contained — it does not embed the SQL parser:

1. It strips SQL comments first — both line comments (`-- … end-of-line`) and block comments
   (`/* … */`, including across newlines) — so a leading comment cannot hide the real first
   keyword. Block comments are removed before line comments.
2. It then reads the **maximal leading identifier** and matches it case-insensitively against an
   allow-list: `SELECT`/`SHOW`/`DESCRIBE`/`DESC` for `select_query`, `EXPLAIN`/`SELECT` for
   `explain_query`. Matching the whole identifier means `SELECTED` or `SHOWROOM` never false-match.
3. It rejects a statement that calls `nextval(` or `setval(`. A `SELECT` with no `FROM` can call
   these functions, and both move a sequence counter, so the server treats them as writes. The
   search runs on the raw text and on the comment-stripped text, so neither a comment nor a
   comment-like string literal can hide the call. `currval` and `lastval` only read, so they are
   allowed.

Anything else (INSERT, UPDATE, DELETE, DROP, CREATE, ALTER, RENAME, …) is rejected with a
`CamusException` (code `CADB0400`) **before** execution. This is the hard security boundary and is
covered by `CamusDB.Mcp.Tests/TestStatementGuard.cs`, including comment-smuggling cases such as
`/* SELECT */ INSERT …` and `-- x` + newline + `INSERT …`.

#### Column types for `create_table`

`oid`, `int64`, `float64`, `float32`, `numeric`, `string`, `string(N)`, `bool`, `bytes`, `bytes(N)`,
`date`, `datetime`, `uuid`, `array(T)` for a scalar `T`, and `serial` (an `int64` identity column),
plus the SQL aliases the server accepts (`int`, `smallint`, `float`, `real`, `decimal`, `varchar`,
`char`, `text`, `boolean`, `blob`, `timestamp`, `guid`, `object_id`, `bigserial`). `numeric` has a
fixed precision of 38 and scale of 9, and takes no `(p, s)` arguments. A column with `index: true`
gets a table-level secondary index named `ix_<table>_<column>`.

Mutating tools compose their own SQL from typed inputs. All identifiers (database, table, column
names) are validated against `^[a-zA-Z_][a-zA-Z0-9_]*$` before interpolation. Values are never
interpolated — they flow through the parameterized placeholder channel (`@name`).

## Security notes

1. Against a server with authentication **off** (the CamusDB default), anyone who can launch the
   binary and reach the endpoint has full access — the MCP server inherits that posture.
2. Against an authenticated server, the MCP server acts as exactly one user. Give it its own
   account with the narrowest grants that make the assistant useful (see
   [Privileges](#privileges)); server-side enforcement is the only limit that survives a bug in
   this process.
3. Credentials live in the MCP client's process environment, which is usually a config file on
   disk (`claude_desktop_config.json`, `.mcp.json`). Treat that file as a secret, or hand the
   server a short-lived `CAMUS_MCP_ACCESS_TOKEN` instead of a password.
4. Point the server at a database you are willing to let the assistant modify, and scope a
   sensible default with `CAMUS_MCP_DEFAULT_DATABASE`.
5. The statement-kind allow-list is non-optional and enforced before any statement executes — it
   is the only mechanism preventing `select_query` from running a DROP or INSERT.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
