# CamusDB MCP Server

`CamusDB.Mcp` is a standalone [Model Context Protocol](https://modelcontextprotocol.io) server
that exposes a running CamusDB cluster to MCP-capable AI clients — Claude Desktop, Claude Code,
and any other MCP host. It speaks MCP over **stdio** and lets an assistant explore and drive
CamusDB through a small, typed tool surface instead of hand-rolling requests.

It depends on **only one** CamusDB package: the published
[`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client) provider (0.5.12). There is no
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

A per-request `database` argument on a tool always wins over `CAMUS_MCP_DEFAULT_DATABASE`.
Context-free statements such as `SHOW DATABASES` tolerate an empty database; table-scoped
statements require a real one.

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

1. CamusDB currently has **no per-request authentication**; the MCP server inherits this posture.
   Anyone who can launch the binary and reach the CamusDB endpoint has full access.
2. Point the server at a database you are willing to let the assistant modify, and scope a
   sensible default with `CAMUS_MCP_DEFAULT_DATABASE`.
3. The statement-kind allow-list is non-optional and enforced before any statement executes — it
   is the only mechanism preventing `select_query` from running a DROP or INSERT.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
