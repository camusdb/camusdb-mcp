
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CamusDB.Client;
using CamusDB.Mcp.Client;
using CamusDB.Mcp.Config;
using ModelContextProtocol.Server;

namespace CamusDB.Mcp.Tools;

// -------------------------------------------------------------------------
// Result envelopes (one record per tool)
// -------------------------------------------------------------------------

/// <summary>Result of <c>list_databases</c>: the database names in the cluster.</summary>
public sealed record ListDatabasesResult(string[] Databases);

/// <summary>Result of <c>list_tables</c>: the table names in a database.</summary>
public sealed record ListTablesResult(string[] Tables);

/// <summary>Result of <c>list_branches</c>: one row per branch of the queried root database.</summary>
public sealed record ListBranchesResult(Dictionary<string, object?>[] Branches);

/// <summary>Result of <c>list_ancestors</c>: one row per ancestor of the queried branch database.</summary>
public sealed record ListAncestorsResult(Dictionary<string, object?>[] Ancestors);

/// <summary>
/// Result of <c>list_views</c>: the plain view names, and one row per materialized view with its
/// populated flag and last refresh time.
/// </summary>
public sealed record ListViewsResult(string[] Views, Dictionary<string, object?>[] MaterializedViews);

/// <summary>Result of <c>list_sequences</c>: one row per sequence in a database.</summary>
public sealed record ListSequencesResult(Dictionary<string, object?>[] Sequences);

/// <summary>Result of <c>show_create</c>: the object's name and the DDL statement that recreates it.</summary>
public sealed record ShowCreateResult(string Name, string Ddl);

/// <summary>Result of <c>get_table_statistics</c>: one row per statistics target, discriminated by <c>kind</c>.</summary>
public sealed record GetTableStatisticsResult(Dictionary<string, object?>[] Statistics);

/// <summary>Result of <c>list_indexes</c>: one row per readable index on a table.</summary>
public sealed record ListIndexesResult(Dictionary<string, object?>[] Indexes);

/// <summary>Result of <c>get_table_schema</c>: one row per column of a table.</summary>
public sealed record GetTableSchemaResult(Dictionary<string, object?>[] Columns);

/// <summary>Result of <c>select_query</c>: the (possibly capped) rows and whether more existed.</summary>
public sealed record SelectQueryResult(Dictionary<string, object?>[] Rows, int RowCount, bool Truncated);

/// <summary>Result of <c>explain_query</c>: the plan rows.</summary>
public sealed record ExplainQueryResult(Dictionary<string, object?>[] Plan);

/// <summary>Result of <c>create_database</c>.</summary>
public sealed record CreateDatabaseResult(bool Ok);

/// <summary>Result of <c>create_table</c>.</summary>
public sealed record CreateTableResult(bool Ok);

/// <summary>
/// Result of <c>insert_rows</c>: the number of rows written (inserted plus updated, under an upsert)
/// and, when a RETURNING list was requested, the returned rows; null otherwise.
/// </summary>
public sealed record InsertRowsResult(int Inserted, Dictionary<string, object?>[]? Returned = null);

/// <summary>Result of <c>get_statement_stats</c>: one row per statement shape, and whether more existed.</summary>
public sealed record GetStatementStatsResult(Dictionary<string, object?>[] Statements, bool Truncated);

/// <summary>Result of <c>get_slow_queries</c>: the slow query log entries, newest first.</summary>
public sealed record GetSlowQueriesResult(Dictionary<string, object?>[] Queries, bool Truncated);

/// <summary>Result of <c>get_index_usage</c>: one row per index with its read and write counters.</summary>
public sealed record GetIndexUsageResult(Dictionary<string, object?>[] Indexes, bool Truncated);

/// <summary>Result of <c>list_running_queries</c>: one row per statement that runs now.</summary>
public sealed record ListRunningQueriesResult(Dictionary<string, object?>[] Queries, bool Truncated);

/// <summary>Result of <c>list_connections</c>: one row per open client or peer connection.</summary>
public sealed record ListConnectionsResult(Dictionary<string, object?>[] Connections, bool Truncated);

/// <summary>Result of <c>get_engine_stats</c>: one row per engine metric.</summary>
public sealed record GetEngineStatsResult(Dictionary<string, object?>[] Stats, bool Truncated);

/// <summary>
/// Result of <c>get_settings</c>: the effective configuration of the node that answers, and the
/// cluster settings that a <c>SET CLUSTER SETTING</c> changed.
/// </summary>
public sealed record GetSettingsResult(
    Dictionary<string, object?>[] Variables, Dictionary<string, object?>[] ClusterOverrides, bool Truncated);

/// <summary>Result of <c>list_extensions</c>: one row per extension build the node loaded.</summary>
public sealed record ListExtensionsResult(Dictionary<string, object?>[] Extensions, bool Truncated);

// -------------------------------------------------------------------------
// Column definition input model for create_table
// -------------------------------------------------------------------------

/// <summary>Typed column definition supplied to <c>create_table</c>.</summary>
public sealed class ColumnDefinition
{
    [Description("Column name (valid SQL identifier).")]
    public string Name { get; set; } = "";

    [Description(CamusDbTools.AcceptedTypesDescription)]
    public string Type { get; set; } = "";

    [Description("If true, this column is part of the PRIMARY KEY.")]
    public bool Primary { get; set; }

    [Description("If true, adds a NOT NULL constraint.")]
    public bool NotNull { get; set; }

    [Description("If true, creates a secondary index on this column (as a table-level KEY ix_<table>_<column>).")]
    public bool Index { get; set; }

    [Description("If true, the column gets a UNIQUE constraint (a unique secondary index). Index need not also be set.")]
    public bool Unique { get; set; }

    [Description("SQL default value expression (e.g. '0', 'true', \"'hello'\"). Null means no default.")]
    public string? Default { get; set; }
}

// -------------------------------------------------------------------------
// Tool class
// -------------------------------------------------------------------------

/// <summary>
/// MCP tool implementations for CamusDB. All tools are stateless autocommit adapters over the
/// <c>CamusDB.Client</c> provider. Read tools never execute mutating SQL (enforced by
/// <see cref="StatementGuard"/>); mutating tools compose SQL from typed inputs and validate
/// identifiers before interpolation, passing values only through the parameterized channel.
/// </summary>
[McpServerToolType]
public sealed class CamusDbTools
{
    /// <summary>The type list shown to the client in the <c>create_table</c> descriptions and errors.</summary>
    internal const string AcceptedTypesDescription =
        "SQL type: oid, int64, float64, float32, numeric, string, string(N), bool, bytes, bytes(N), date, " +
        "datetime, uuid, array(T) for a scalar T, or serial. Aliases: object_id; int, smallint; float, real; " +
        "decimal; varchar, char, text (also sized); boolean; blob (also sized); timestamp; guid; bigserial. " +
        "numeric has a fixed precision of 38 and scale of 9 and takes no (p, s) arguments.";

    // Mirrors field_type in the CamusDB grammar (plus the SERIAL / BIGSERIAL shorthand), so a type
    // the server would parse is accepted here and anything else is refused before SQL composition.
    // The type text is interpolated into the CREATE TABLE statement, so this match is also what keeps
    // it from carrying anything but a type.
    private const string ScalarTypePattern =
        "oid|object_id|int64|int|smallint|float64|float32|float|real|bool|boolean|date|datetime|timestamp|" +
        "uuid|guid|numeric|decimal|(?:string|varchar|char|text|bytes|blob)(?:\\s*\\(\\s*\\d+\\s*\\))?";

    private static readonly Regex ColumnTypeRegex = new(
        $"^(?:(?:{ScalarTypePattern})|array\\s*\\(\\s*(?:{ScalarTypePattern})\\s*\\)|serial|bigserial)\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // -------------------------------------------------------------------------
    // Read tools
    // -------------------------------------------------------------------------

    /// <summary>Lists all databases in the CamusDB cluster.</summary>
    [McpServerTool]
    [Description("List all databases in the CamusDB cluster.")]
    public static async Task<ListDatabasesResult> ListDatabases(
        CamusClient camus,
        CancellationToken ct)
    {
        QueryResult result = await camus.QueryAsync(null, "SHOW DATABASES", null, 0, ct).ConfigureAwait(false);
        string[] names = result.Rows
            .Select(r => GetStringField(r, "Database"))
            .Where(n => n.Length > 0)
            .ToArray();
        return new ListDatabasesResult(names);
    }

    /// <summary>Lists all tables in the given database.</summary>
    [McpServerTool]
    [Description("List all tables in a database.")]
    public static async Task<ListTablesResult> ListTables(
        CamusClient camus,
        [Description("Database name.")] string database,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        QueryResult result = await camus.QueryAsync(database, "SHOW TABLES", null, 0, ct).ConfigureAwait(false);
        string[] names = result.Rows
            .Select(r => GetStringField(r, "tables", "Table", "table"))
            .Where(n => n.Length > 0)
            .ToArray();
        return new ListTablesResult(names);
    }

    /// <summary>Lists all branches of a root database.</summary>
    [McpServerTool]
    [Description("List all branches of a root database. Returns an empty list if no branches exist. The database parameter is the root database whose branches to list; an unknown name surfaces a DatabaseDoesntExist error.")]
    public static async Task<ListBranchesResult> ListBranches(
        CamusClient camus,
        [Description("Root database name whose branches to list.")] string database,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        QueryResult result = await camus.QueryAsync(database, $"SHOW BRANCHES FROM {database}", null, 0, ct).ConfigureAwait(false);
        return new ListBranchesResult([.. result.Rows.Select(BranchToRow)]);
    }

    /// <summary>Lists all readable indexes on a table.</summary>
    [McpServerTool]
    [Description("List all readable indexes on a table. Indexes still being backfilled are omitted.")]
    public static async Task<ListIndexesResult> ListIndexes(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name.")] string table,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(table, "table");
        QueryResult result = await camus.QueryAsync(database, $"SHOW INDEXES FROM {table}", null, 0, ct).ConfigureAwait(false);
        return new ListIndexesResult([.. result.Rows]);
    }

    /// <summary>Returns the column schema for a table.</summary>
    [McpServerTool]
    [Description("Get detailed schema for a table (column names, types, nullability, primary key, indexes, defaults).")]
    public static async Task<GetTableSchemaResult> GetTableSchema(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name.")] string table,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(table, "table");
        QueryResult result = await camus.QueryAsync(database, $"SHOW COLUMNS FROM {table}", null, 0, ct).ConfigureAwait(false);
        return new GetTableSchemaResult([.. result.Rows]);
    }

    /// <summary>Lists the ancestors of a branch database.</summary>
    [McpServerTool]
    [Description("List the ancestors of a branch database: its parent, grandparent and so on up to the root (depth 1 = immediate parent). Returns an empty list for a root database.")]
    public static async Task<ListAncestorsResult> ListAncestors(
        CamusClient camus,
        [Description("Branch database name whose ancestors to list.")] string database,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        QueryResult result = await camus.QueryAsync(database, $"SHOW ANCESTORS FROM {database}", null, 0, ct).ConfigureAwait(false);
        return new ListAncestorsResult([.. result.Rows.Select(AncestorToRow)]);
    }

    /// <summary>Lists the views and materialized views in a database.</summary>
    [McpServerTool]
    [Description("List the views and materialized views in a database. Each materialized view reports whether it is populated and when it was last refreshed.")]
    public static async Task<ListViewsResult> ListViews(
        CamusClient camus,
        [Description("Database name.")] string database,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        QueryResult views = await camus.QueryAsync(database, "SHOW VIEWS", null, 0, ct).ConfigureAwait(false);
        QueryResult materialized = await camus.QueryAsync(database, "SHOW MATERIALIZED VIEWS", null, 0, ct).ConfigureAwait(false);

        string[] names = views.Rows
            .Select(r => GetStringField(r, "views", "view"))
            .Where(n => n.Length > 0)
            .ToArray();
        return new ListViewsResult(names, [.. materialized.Rows]);
    }

    /// <summary>Lists the sequences in a database.</summary>
    [McpServerTool]
    [Description("List the sequences in a database with their options (start_value, increment, min_value, max_value, cache, owned_by, comment). reserved_upto is an upper bound on the values issued so far, not the last value.")]
    public static async Task<ListSequencesResult> ListSequences(
        CamusClient camus,
        [Description("Database name.")] string database,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        QueryResult result = await camus.QueryAsync(database, "SHOW SEQUENCES", null, 0, ct).ConfigureAwait(false);
        return new ListSequencesResult([.. result.Rows]);
    }

    /// <summary>Returns the DDL statement that recreates a table, view, materialized view or sequence.</summary>
    [McpServerTool]
    [Description("Get the DDL statement that recreates an object (SHOW CREATE TABLE / VIEW / MATERIALIZED VIEW / SEQUENCE). Kind is 'table' (default), 'view', 'materialized_view' or 'sequence'.")]
    public static async Task<ShowCreateResult> ShowCreate(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Object name.")] string name,
        [Description("Object kind: 'table' (default), 'view', 'materialized_view' or 'sequence'.")] string? kind = null,
        [Description("Tables only: if true, omits the secondary indexes (SHOW CREATE TABLE ... WITHOUT INDEXES). The primary key is always included.")] bool withoutIndexes = false,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(name, "name");

        string sql = (kind?.Trim().ToLowerInvariant() ?? "table") switch
        {
            "" or "table" => withoutIndexes ? $"SHOW CREATE TABLE {name} WITHOUT INDEXES" : $"SHOW CREATE TABLE {name}",
            "view" => $"SHOW CREATE VIEW {name}",
            "materialized_view" or "materialized view" => $"SHOW CREATE MATERIALIZED VIEW {name}",
            "sequence" => $"SHOW CREATE SEQUENCE {name}",
            _ => throw new CamusException("CADB0400",
                $"Unknown object kind '{kind}'. Accepted: table, view, materialized_view, sequence."),
        };

        QueryResult result = await camus.QueryAsync(database, sql, null, 0, ct).ConfigureAwait(false);
        if (result.Rows.Count == 0)
            throw new CamusException("CADB0400", $"SHOW CREATE returned no row for '{name}'.");

        // The DDL column is named after the statement ("Create Table", "create view", ...), so it is
        // found by its prefix rather than by one fixed name.
        Dictionary<string, object?> row = result.Rows[0];
        string ddl = row
            .Where(kv => kv.Key.StartsWith("create", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value?.ToString() ?? "")
            .FirstOrDefault() ?? "";
        return new ShowCreateResult(name, ddl);
    }

    /// <summary>Returns the optimizer statistics for a table.</summary>
    [McpServerTool]
    [Description("Get the optimizer statistics for a table (SHOW STATISTICS FOR TABLE): one row per target, discriminated by 'kind' (table counters, per-column estimates, composite-key estimates, per-index entry counts). Columns that do not apply to a row's kind are null. Check last_analyzed before trusting the values; run ANALYZE out of band to refresh them.")]
    public static async Task<GetTableStatisticsResult> GetTableStatistics(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name.")] string table,
        CancellationToken ct)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(table, "table");
        QueryResult result = await camus.QueryAsync(database, $"SHOW STATISTICS FOR TABLE {table}", null, 0, ct).ConfigureAwait(false);
        return new GetTableStatisticsResult([.. result.Rows]);
    }

    // -------------------------------------------------------------------------
    // Query tools
    // -------------------------------------------------------------------------

    /// <summary>Executes a read-only SELECT or SHOW statement.</summary>
    [McpServerTool]
    [Description("Execute a SELECT, SHOW or DESCRIBE statement, including UNION / INTERSECT / EXCEPT [ALL] set operations. Mutating statements (INSERT, UPDATE, DELETE, DROP, CREATE) and row-locking reads (SELECT ... FOR UPDATE) are rejected before execution. Results are capped at max_rows (default 1000). NUMERIC values are returned as their exact decimal text. Parameters may be JSON scalars or arrays of scalars (bound as array parameters).")]
    public static async Task<SelectQueryResult> SelectQuery(
        CamusClient camus,
        McpConfig config,
        [Description("Database name (required for table queries; may be empty for SHOW DATABASES).")] string? database,
        [Description("SQL SELECT, SHOW or DESCRIBE statement to execute.")] string sql,
        [Description("Named parameters referenced in the SQL with @name placeholders. Values are JSON scalars or arrays of scalars. A number with more than 15 significant digits binds as an exact NUMERIC.")] Dictionary<string, JsonElement>? parameters = null,
        [Description("Maximum rows to return (default 1000, hard cap).")] int? maxRows = null,
        CancellationToken ct = default)
    {
        // Security: classify and reject mutating statements before any execution.
        StatementGuard.AssertReadOnly(sql);

        int cap = Math.Min(maxRows ?? config.MaxRows, config.MaxRows);
        QueryResult result = await camus.QueryAsync(database, sql, parameters, cap, ct).ConfigureAwait(false);
        return new SelectQueryResult([.. result.Rows], result.Rows.Count, result.Truncated);
    }

    /// <summary>Executes an EXPLAIN statement to get the query plan.</summary>
    [McpServerTool]
    [Description("Execute an EXPLAIN statement. Mode can be 'plan' (default), 'logical', 'physical', or 'analyze'. If the sql argument already starts with EXPLAIN, it is forwarded as-is; otherwise EXPLAIN is prepended. 'analyze' runs the SELECT and reports measured per-operator statistics; it is unsupported on a FROM-less SELECT, and surfaces as a tool error there. A row-locking read (FOR UPDATE) is accepted in the plan modes, which take no lock, and rejected under 'analyze', which takes the locks.")]
    public static async Task<ExplainQueryResult> ExplainQuery(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("SQL to explain (SELECT statement or full EXPLAIN statement).")] string sql,
        [Description("Plan mode: 'plan' (default), 'logical', 'physical', or 'analyze'.")] string? mode = null,
        CancellationToken ct = default)
    {
        string keyword = StatementGuard.AssertExplain(sql, mode);

        string explainSql;
        if (keyword == "EXPLAIN")
        {
            explainSql = sql;
        }
        else
        {
            // CamusDB grammar: EXPLAIN (LOGICAL) / EXPLAIN (PHYSICAL) / EXPLAIN (ANALYZE) / EXPLAIN
            string prefix = mode?.ToLowerInvariant() switch
            {
                "logical" => "EXPLAIN (LOGICAL)",
                "physical" => "EXPLAIN (PHYSICAL)",
                "analyze" => "EXPLAIN (ANALYZE)",
                _ => "EXPLAIN",
            };
            explainSql = $"{prefix} {sql}";
        }

        QueryResult result = await camus.QueryAsync(database, explainSql, null, 0, ct).ConfigureAwait(false);
        return new ExplainQueryResult([.. result.Rows]);
    }

    // -------------------------------------------------------------------------
    // Mutating tools
    // -------------------------------------------------------------------------

    /// <summary>Creates a new database.</summary>
    [McpServerTool]
    [Description("Create a new database. Composes and executes CREATE DATABASE [IF NOT EXISTS] <name>.")]
    public static async Task<CreateDatabaseResult> CreateDatabase(
        CamusClient camus,
        [Description("Database name (valid SQL identifier).")] string name,
        [Description("If true, uses CREATE DATABASE IF NOT EXISTS (no error if the database already exists).")] bool ifNotExists = false,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(name, "database name");
        await camus.CreateDatabaseAsync(name, ifNotExists, ct).ConfigureAwait(false);
        return new CreateDatabaseResult(Ok: true);
    }

    /// <summary>Creates a new table from a typed column definition list.</summary>
    [McpServerTool]
    [Description("Create a new table. Columns are specified as a typed list; identifiers and types are validated before SQL composition. Accepted types: oid, int64, float64, float32, numeric, string, string(N), bool, bytes, bytes(N), date, datetime, uuid, array(T) for a scalar T, serial (an int64 identity column), and their SQL aliases.")]
    public static async Task<CreateTableResult> CreateTable(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name (valid SQL identifier).")] string name,
        [Description("Column definitions.")] IReadOnlyList<ColumnDefinition> columns,
        [Description("If true, uses CREATE TABLE IF NOT EXISTS.")] bool ifNotExists = false,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(name, "table name");

        if (columns == null || columns.Count == 0)
            throw new CamusException("CADB0400", "At least one column is required.");

        StringBuilder sb = new();
        sb.Append(ifNotExists ? "CREATE TABLE IF NOT EXISTS " : "CREATE TABLE ");
        sb.Append(name);
        sb.Append(" (");

        // CamusDB has no column-level INDEX constraint: a non-unique secondary index is a table-level
        // KEY <name> (<column>) item, appended after the columns.
        List<string> indexedColumns = [];

        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDefinition col = columns[i];
            IdentifierValidator.Validate(col.Name, "column name");
            ValidateSqlType(col.Type);

            if (i > 0) sb.Append(", ");
            sb.Append(col.Name);
            sb.Append(' ');
            sb.Append(col.Type);

            if (col.Primary) sb.Append(" PRIMARY KEY");
            else
            {
                if (col.NotNull) sb.Append(" NOT NULL");
                if (col.Unique) sb.Append(" UNIQUE");
                else if (col.Index) indexedColumns.Add(col.Name);
            }

            if (col.Default is not null)
            {
                sb.Append(" DEFAULT(");
                sb.Append(col.Default);
                sb.Append(')');
            }
        }

        foreach (string column in indexedColumns)
        {
            string indexName = $"ix_{name}_{column}";
            IdentifierValidator.Validate(indexName, "index name");
            sb.Append(", KEY ");
            sb.Append(indexName);
            sb.Append(" (");
            sb.Append(column);
            sb.Append(')');
        }

        sb.Append(')');

        await camus.ExecuteNonQueryAsync(database, sb.ToString(), null, ct).ConfigureAwait(false);
        return new CreateTableResult(Ok: true);
    }

    /// <summary>Inserts rows into a table using parameterized INSERT statements.</summary>
    [McpServerTool]
    [Description("Insert one or more rows into a table. Rows are a list of column→value objects. Values are passed as SQL parameters (no string interpolation). Large batches are chunked to respect the per-transaction mutation limit; each chunk commits on its own. onConflict sets what happens to a row whose unique key is already taken: 'error' (default) fails the chunk, 'do_nothing' skips the row (INSERT ... ON CONFLICT DO NOTHING), and 'upsert' updates the stored row with the row's values (UPSERT INTO, or ON CONFLICT (conflictColumns) DO UPDATE). With 'upsert', a key must appear only once in a chunk of 500 rows. returning lists columns to read back from each written row, such as generated ids. Returns the number of rows written: inserted plus updated rows; skipped rows are not counted.")]
    public static async Task<InsertRowsResult> InsertRows(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name.")] string table,
        [Description("Rows to insert. Each row is an object mapping column names to values (string, number, bool, or null).")] IReadOnlyList<Dictionary<string, JsonElement>> rows,
        [Description("What to do with a row whose unique key is already taken: 'error' (default), 'do_nothing' or 'upsert'. 'upsert' needs the UPDATE privilege as well as INSERT.")] string? onConflict = null,
        [Description("The columns of the unique index that decides a conflict (the arbiter), for example ['email']. Omitted: 'do_nothing' uses every unique index and 'upsert' uses the primary key. With 'upsert', every inserted column that is not listed here is updated.")] IReadOnlyList<string>? conflictColumns = null,
        [Description("Columns to return from each written row, or ['*'] for every column. The values are the stored values, so they include defaults such as gen_id(), sequence and serial values. Under 'upsert' an updated row returns its new values. Needs the SELECT privilege.")] IReadOnlyList<string>? returning = null,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(table, "table");

        string action = ParseConflictAction(onConflict);
        string[] arbiter = [.. conflictColumns ?? []];
        foreach (string col in arbiter)
            IdentifierValidator.Validate(col, "conflict column");
        if (action == "error" && arbiter.Length > 0)
            throw new CamusException("CADB0400", "conflictColumns needs onConflict 'do_nothing' or 'upsert'.");

        string returningClause = BuildReturningClause(returning);

        if (rows == null || rows.Count == 0)
            return new InsertRowsResult(0, returningClause.Length > 0 ? [] : null);

        // Use first row to get column names; validate all column names.
        string[] columns = [.. rows[0].Keys];
        foreach (string col in columns)
            IdentifierValidator.Validate(col, "column name");

        // UPSERT INTO is the server's short form of ON CONFLICT (<primary key>) DO UPDATE, so it is
        // used when no arbiter is named; a named arbiter needs the explicit clause.
        string verb = action == "upsert" && arbiter.Length == 0 ? "UPSERT INTO " : "INSERT INTO ";
        string suffix = BuildConflictClause(action, columns, arbiter) + returningClause;

        int totalInserted = 0;
        List<Dictionary<string, object?>>? returned = returningClause.Length > 0 ? [] : null;

        // Chunk to stay within MaxMutationsPerTransaction. 500 rows per batch is safe for
        // tables with up to 40 secondary indexes (500 * 40 = 20,000 = MaxMutationsPerTransaction).
        const int ChunkSize = 500;
        foreach (IEnumerable<Dictionary<string, JsonElement>> chunk in Chunk(rows, ChunkSize))
        {
            List<Dictionary<string, JsonElement>> rowList = chunk.ToList();
            string sql = BuildInsertSql(verb, table, columns, rowList) + suffix;
            Dictionary<string, JsonElement> parameters = BuildInsertParameters(columns, rowList);

            if (returned is null)
            {
                totalInserted += await camus.ExecuteNonQueryAsync(database, sql, parameters, ct).ConfigureAwait(false);
                continue;
            }

            // The row count of a RETURNING statement always equals the number of returned rows.
            QueryResult result = await camus.ExecuteReturningAsync(database, sql, parameters, ct).ConfigureAwait(false);
            returned.AddRange(result.Rows);
            totalInserted += result.Rows.Count;
        }

        return new InsertRowsResult(totalInserted, returned?.ToArray());
    }

    // -------------------------------------------------------------------------
    // Diagnostic tools
    // -------------------------------------------------------------------------

    /// <summary>Returns the per-shape statement statistics of the node.</summary>
    [McpServerTool]
    [Description("Get the statement statistics of the node that answers (SHOW STATEMENT STATS): one row per statement shape with calls, total_ms, min_ms and mean_ms, rows read and returned, full scans, spills, errors, retries and plan changes. Use it to find the statements that cost the most in total, not only the slow ones. Off by default: an operator turns it on with statement_stats_enabled. Requires a superuser when authentication is enabled. The totals are in memory and one node's view only.")]
    public static async Task<GetStatementStatsResult> GetStatementStats(
        CamusClient camus,
        McpConfig config,
        [Description("Optional LIKE pattern on the statement text, for example '%FROM orders%'.")] string? like = null,
        CancellationToken ct = default)
    {
        QueryResult result = await QueryDiagnosticAsync(camus, config, null, "SHOW STATEMENT STATS" + LikeClause(like), ct).ConfigureAwait(false);
        return new GetStatementStatsResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Returns the slow query log of the node.</summary>
    [McpServerTool]
    [Description("Get the slow query log of the node that answers (SHOW SLOW QUERIES): statements that ran at or above slow_query_log_threshold_ms, newest first, with their duration, database and text. Off by default: an operator turns it on with slow_query_log_enabled. Requires a superuser when authentication is enabled. The log is in memory and one node's view only.")]
    public static async Task<GetSlowQueriesResult> GetSlowQueries(
        CamusClient camus,
        McpConfig config,
        [Description("Optional LIKE pattern on the statement text, for example '%FROM orders%'.")] string? like = null,
        CancellationToken ct = default)
    {
        QueryResult result = await QueryDiagnosticAsync(camus, config, null, "SHOW SLOW QUERIES" + LikeClause(like), ct).ConfigureAwait(false);
        return new GetSlowQueriesResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Returns the read and write counters of the indexes in a database.</summary>
    [McpServerTool]
    [Description("Get index usage counters (SHOW INDEX USAGE): for each index, how often statements read it (reads), how often a constraint check read it (constraint_reads), and how often writes maintained it (writes), since tracked_since. An index with reads = 0 and constraint_reads = 0 but writes > 0 over a long window is a candidate to drop; a unique index or primary key enforces a constraint and is not removable for that reason. Requires a superuser when authentication is enabled. The counters are in memory and one node's view only, so an index is unused only if it is unused on every node.")]
    public static async Task<GetIndexUsageResult> GetIndexUsage(
        CamusClient camus,
        McpConfig config,
        [Description("Database name.")] string database,
        [Description("Optional table name. Omitted: every index of every table in the database.")] string? table = null,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(database, "database");
        string sql = "SHOW INDEX USAGE";
        if (!string.IsNullOrEmpty(table))
        {
            IdentifierValidator.Validate(table, "table");
            sql += $" FROM {table}";
        }

        QueryResult result = await QueryDiagnosticAsync(camus, config, database, sql, ct).ConfigureAwait(false);
        return new GetIndexUsageResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Lists the statements that run now.</summary>
    [McpServerTool]
    [Description("List the statements that run now (SHOW QUERIES, or SHOW CLUSTER QUERIES with cluster = true), with their query_id, user, database, transaction, phase, elapsed_ms and text. A superuser sees every statement; another user sees only their own. In the cluster form, a node that does not answer gives one row with its error column set.")]
    public static async Task<ListRunningQueriesResult> ListRunningQueries(
        CamusClient camus,
        McpConfig config,
        [Description("Optional LIKE pattern on the statement text, for example '%FROM orders%'.")] string? like = null,
        [Description("If true, collects the statements from every node of the cluster.")] bool cluster = false,
        CancellationToken ct = default)
    {
        string sql = (cluster ? "SHOW CLUSTER QUERIES" : "SHOW QUERIES") + LikeClause(like);
        QueryResult result = await QueryDiagnosticAsync(camus, config, null, sql, ct).ConfigureAwait(false);
        return new ListRunningQueriesResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Lists the open connections.</summary>
    [McpServerTool]
    [Description("List the open client and peer connections (SHOW CONNECTIONS, or SHOW CLUSTER CONNECTIONS with cluster = true), with protocol, user, age_ms, idle_ms, requests and active_queries. A superuser sees every connection; another user sees only their own.")]
    public static async Task<ListConnectionsResult> ListConnections(
        CamusClient camus,
        McpConfig config,
        [Description("If true, collects the connections from every node of the cluster.")] bool cluster = false,
        CancellationToken ct = default)
    {
        string sql = cluster ? "SHOW CLUSTER CONNECTIONS" : "SHOW CONNECTIONS";
        QueryResult result = await QueryDiagnosticAsync(camus, config, null, sql, ct).ConfigureAwait(false);
        return new ListConnectionsResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Returns the engine metrics of the node.</summary>
    [McpServerTool]
    [Description("Get the engine metrics of the node that answers (SHOW ENGINE STATS): counters and latency distributions of the storage, Raft and execution layers. Two readings a short time apart show a rate. Requires a superuser when authentication is enabled.")]
    public static async Task<GetEngineStatsResult> GetEngineStats(
        CamusClient camus,
        McpConfig config,
        [Description("Optional LIKE pattern on the metric name, for example 'raft.executor%'.")] string? like = null,
        CancellationToken ct = default)
    {
        QueryResult result = await QueryDiagnosticAsync(camus, config, null, "SHOW ENGINE STATS" + LikeClause(like), ct).ConfigureAwait(false);
        return new GetEngineStatsResult([.. result.Rows], result.Truncated);
    }

    /// <summary>Returns the effective configuration of the node and the cluster setting overrides.</summary>
    [McpServerTool]
    [Description("Get the server settings. 'variables' (SHOW VARIABLES) is the configuration the node that answers runs: one row per setting with its value, type, default, source (default, config, env, cli or cluster), mutability (runtime or restart) and scope (cluster or node); secrets are masked. 'clusterOverrides' (SHOW CLUSTER SETTINGS) lists only the settings that a SET CLUSTER SETTING changed for the whole cluster, so it is empty until someone changes one. This tool only reads settings. Requires a superuser when authentication is enabled.")]
    public static async Task<GetSettingsResult> GetSettings(
        CamusClient camus,
        McpConfig config,
        [Description("Optional LIKE pattern on the setting name, for example 'slow_query%'.")] string? like = null,
        CancellationToken ct = default)
    {
        string likeClause = LikeClause(like);
        QueryResult variables = await QueryDiagnosticAsync(camus, config, null, "SHOW VARIABLES" + likeClause, ct).ConfigureAwait(false);
        QueryResult overrides = await QueryDiagnosticAsync(camus, config, null, "SHOW CLUSTER SETTINGS" + likeClause, ct).ConfigureAwait(false);
        return new GetSettingsResult([.. variables.Rows], [.. overrides.Rows], variables.Truncated || overrides.Truncated);
    }

    /// <summary>Lists the extension builds the node loaded.</summary>
    [McpServerTool]
    [Description("List the C# extension builds the node loaded (SHOW EXTENSIONS): name, version, trust, sha256 and the functions, triggers, procedures, aggregates and table functions each one registered, with their SQL signatures. With a database, status is 'installed' for the build that database uses, 'available' for the others, and 'missing' for an extension the database installed that this node does not load. Node-local; requires a superuser when authentication is enabled.")]
    public static async Task<ListExtensionsResult> ListExtensions(
        CamusClient camus,
        McpConfig config,
        [Description("Optional database name, to see which extensions that database installed.")] string? database = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(database))
            IdentifierValidator.Validate(database, "database");

        QueryResult result = await QueryDiagnosticAsync(camus, config, NullIfEmpty(database), "SHOW EXTENSIONS", ct).ConfigureAwait(false);
        return new ListExtensionsResult([.. result.Rows], result.Truncated);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static Task<QueryResult> QueryDiagnosticAsync(
        CamusClient camus, McpConfig config, string? database, string sql, CancellationToken ct) =>
        camus.QueryAsync(database, sql, null, config.MaxRows, ct);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string LikeClause(string? like) =>
        string.IsNullOrEmpty(like) ? "" : " LIKE " + QuoteLiteral(like, "like pattern");

    /// <summary>
    /// Quotes <paramref name="value"/> as a plain single-quoted SQL string literal, with each quote
    /// doubled. The SHOW ... LIKE grammar takes a literal, not a placeholder, so this is the only way
    /// the value reaches the statement. The CamusDB lexer does not accept a control character in a
    /// plain literal, and a backslash in one is an ordinary character, so a value with a control
    /// character is refused and nothing else can end the literal early.
    /// </summary>
    public static string QuoteLiteral(string value, string what)
    {
        foreach (char c in value)
        {
            if (c is '\a' or '\b' or '\f' or '\n' or '\r' or '\t' or '\v' or '\0')
                throw new CamusException("CADB0400", $"The {what} must not contain control characters.");
        }

        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Normalizes the <c>onConflict</c> input of <c>insert_rows</c> to <c>error</c>,
    /// <c>do_nothing</c> or <c>upsert</c>. Throws <see cref="CamusException"/> with code
    /// <c>CADB0400</c> for anything else.
    /// </summary>
    public static string ParseConflictAction(string? onConflict) =>
        (onConflict?.Trim().ToLowerInvariant() ?? "") switch
        {
            "" or "error" => "error",
            "do_nothing" or "do nothing" or "nothing" => "do_nothing",
            "upsert" or "update" or "do_update" or "do update" => "upsert",
            _ => throw new CamusException("CADB0400",
                $"Unknown onConflict value '{onConflict}'. Accepted: error, do_nothing, upsert."),
        };

    /// <summary>
    /// Composes the ON CONFLICT clause of <c>insert_rows</c> (with a leading space), or an empty
    /// string when none is needed: for <c>error</c>, and for an <c>upsert</c> with no arbiter, which
    /// is sent as UPSERT INTO. An upsert whose columns are all in the arbiter has nothing to update,
    /// so it leaves a taken key's row as it is, as UPSERT INTO does.
    /// </summary>
    public static string BuildConflictClause(string action, IReadOnlyList<string> columns, IReadOnlyList<string> arbiter)
    {
        if (action == "error" || (action == "upsert" && arbiter.Count == 0))
            return "";

        string target = arbiter.Count > 0 ? $" ({string.Join(", ", arbiter)})" : "";
        string[] updated = action == "upsert"
            ? [.. columns.Where(c => !arbiter.Contains(c, StringComparer.OrdinalIgnoreCase))]
            : [];

        if (updated.Length == 0)
            return $" ON CONFLICT{target} DO NOTHING";

        return $" ON CONFLICT{target} DO UPDATE SET " +
               string.Join(", ", updated.Select(c => $"{c} = excluded.{c}"));
    }

    /// <summary>
    /// Composes the RETURNING clause of <c>insert_rows</c> (with a leading space), or an empty string
    /// when no column is requested. Each item must be <c>*</c> or a valid identifier, so no expression
    /// is ever interpolated.
    /// </summary>
    public static string BuildReturningClause(IReadOnlyList<string>? returning)
    {
        if (returning is null || returning.Count == 0)
            return "";

        foreach (string item in returning)
        {
            if (item != "*")
                IdentifierValidator.Validate(item, "returning column");
        }

        return " RETURNING " + string.Join(", ", returning);
    }

    private static string BuildInsertSql(string verb, string table, string[] columns, List<Dictionary<string, JsonElement>> rows)
    {
        StringBuilder sb = new();
        sb.Append(verb);
        sb.Append(table);
        sb.Append(" (");
        sb.Append(string.Join(", ", columns));
        sb.Append(") VALUES ");

        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) sb.Append(", ");
            sb.Append('(');
            for (int c = 0; c < columns.Length; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append('@');
                sb.Append(ParamName(r, columns[c]));
            }
            sb.Append(')');
        }

        return sb.ToString();
    }

    private static Dictionary<string, JsonElement> BuildInsertParameters(
        string[] columns, List<Dictionary<string, JsonElement>> rows)
    {
        Dictionary<string, JsonElement> parameters = new();
        for (int r = 0; r < rows.Count; r++)
        {
            foreach (string col in columns)
            {
                JsonElement elem = rows[r].TryGetValue(col, out JsonElement v) ? v : default;
                parameters["@" + ParamName(r, col)] = elem;
            }
        }
        return parameters;
    }

    private static string ParamName(int rowIndex, string column) => $"r{rowIndex}_{column}";

    /// <summary>
    /// Validates a <c>create_table</c> column type against the types the CamusDB grammar accepts.
    /// Throws <see cref="CamusException"/> with code <c>CADB0400</c> for anything else.
    /// </summary>
    public static void ValidateSqlType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new CamusException("CADB0400", "Column type must not be empty.");
        if (ColumnTypeRegex.IsMatch(type))
            return;
        throw new CamusException("CADB0400", $"Unknown column type '{type}'. {AcceptedTypesDescription}");
    }

    private static IEnumerable<IEnumerable<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size);
    }

    /// <summary>
    /// Reads a string cell from a materialized row, trying the candidate column names in order and
    /// falling back to a case-insensitive match. Returns an empty string when absent or null.
    /// </summary>
    private static string GetStringField(Dictionary<string, object?> row, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (row.TryGetValue(candidate, out object? value) && value is not null)
                return value.ToString() ?? "";
        }

        foreach (string candidate in candidates)
        {
            foreach ((string key, object? value) in row)
            {
                if (string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase) && value is not null)
                    return value.ToString() ?? "";
            }
        }

        return "";
    }

    // The branch tools send SHOW BRANCHES / SHOW ANCESTORS as SQL, which is also what the client's
    // ShowBranchesAsync/ShowAncestorsAsync send since 0.16.7 (earlier clients called REST routes the
    // server never had). The rows keep the camelCase forkTimestamp key the tool returned before, so
    // its output is unchanged.
    private static Dictionary<string, object?> BranchToRow(Dictionary<string, object?> row) => new()
    {
        ["database"] = row.GetValueOrDefault("database"),
        ["id"] = row.GetValueOrDefault("id"),
        ["depth"] = row.GetValueOrDefault("depth"),
        ["parent"] = row.GetValueOrDefault("parent"),
        ["forkTimestamp"] = row.GetValueOrDefault("fork_timestamp"),
    };

    // SHOW ANCESTORS has no parent column: each row is itself a step up the parent chain.
    private static Dictionary<string, object?> AncestorToRow(Dictionary<string, object?> row) => new()
    {
        ["database"] = row.GetValueOrDefault("database"),
        ["id"] = row.GetValueOrDefault("id"),
        ["depth"] = row.GetValueOrDefault("depth"),
        ["forkTimestamp"] = row.GetValueOrDefault("fork_timestamp"),
    };
}
