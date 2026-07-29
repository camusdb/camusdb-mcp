# CamusDB MCP Server

`CamusDB.Mcp` is a standalone [Model Context Protocol](https://modelcontextprotocol.io) server
that exposes a running CamusDB cluster to MCP-capable AI clients — Claude Desktop, Claude Code,
and any other MCP host. It speaks MCP over **stdio** and lets an assistant explore and drive
CamusDB through a small, typed tool surface instead of hand-rolling requests.

It depends on **only one** CamusDB package: the published
[`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client) provider (0.9.3). There is no
dependency on `CamusDB.Core` and no project reference to the engine — the server talks to CamusDB
exclusively through the client's connection/command/reader API.

## How it works

Each tool call opens a short-lived `CamusConnection` (autocommit — no transaction is carried
between calls) scoped to the requested database and issues the statement through the real client
API: reader queries for reads, non-query execution for DML/DDL, and the client's dedicated
`CreateDatabaseAsync` / `ShowBranchesAsync` entry points where they exist. Rows are read out of
`CamusDataReader` column by column into plain `Dictionary<string, object?>` maps.

Read tools classify the SQL locally (see [Security](#read-only-vs-mutating-boundary)) before any
statement reaches the server.

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
| `list_branches`    | List all branches of a root database. Returns an empty list if none exist.                  |
| `list_indexes`     | List all readable indexes on a table (via `SHOW INDEXES FROM`). Mid-backfill indexes omitted.|
| `get_table_schema` | Get column schema for a table (via `SHOW COLUMNS FROM`): names, types, nullability, defaults.|

### Query tools (read-only, enforced by the statement guard)

| Tool            | Description                                                                                 |
| --------------- | ------------------------------------------------------------------------------------------- |
| `select_query`  | Execute a SELECT or SHOW statement. Mutating SQL is rejected before execution. Capped at `max_rows`. |
| `explain_query` | Execute an EXPLAIN statement (plan / logical / physical). Bare SELECT is prefixed with EXPLAIN. |

### Mutating tools

| Tool              | Description                                                                                |
| ----------------- | ------------------------------------------------------------------------------------------ |
| `create_database` | Create a new database (supports IF NOT EXISTS) via the client's CREATE DATABASE entry point.|
| `create_table`    | Create a table from a typed column definition list. Identifiers are validated.              |
| `insert_rows`     | Insert one or more rows. Values are passed as parameters (no string interpolation), chunked at 500 rows/batch. |

## Read-only vs. mutating boundary

`select_query` and `explain_query` classify the SQL **locally** before any statement reaches the
server. The classifier (`StatementGuard`) is self-contained — it does not embed the SQL parser:

1. It strips SQL comments first — both line comments (`-- … end-of-line`) and block comments
   (`/* … */`, including across newlines) — so a leading comment cannot hide the real first
   keyword. Block comments are removed before line comments.
2. It then reads the **maximal leading identifier** and matches it case-insensitively against an
   allow-list: `SELECT`/`SHOW` for `select_query`, `EXPLAIN`/`SELECT` for `explain_query`.
   Matching the whole identifier means `SELECTED` or `SHOWROOM` never false-match.

Anything else (INSERT, UPDATE, DELETE, DROP, CREATE, ALTER, RENAME, …) is rejected with a
`CamusException` (code `CADB0400`) **before** execution. This is the hard security boundary and is
covered by `CamusDB.Mcp.Tests/TestStatementGuard.cs`, including comment-smuggling cases such as
`/* SELECT */ INSERT …` and `-- x` + newline + `INSERT …`.

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
