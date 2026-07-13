
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

/// <summary>Result of <c>insert_rows</c>: the total number of inserted rows.</summary>
public sealed record InsertRowsResult(int Inserted);

// -------------------------------------------------------------------------
// Column definition input model for create_table
// -------------------------------------------------------------------------

/// <summary>Typed column definition supplied to <c>create_table</c>.</summary>
public sealed class ColumnDefinition
{
    [Description("Column name (valid SQL identifier).")]
    public string Name { get; set; } = "";

    [Description("SQL type: oid, int64, float64, float32, string(N), bool, bytes, date, datetime, uuid.")]
    public string Type { get; set; } = "";

    [Description("If true, this column is part of the PRIMARY KEY.")]
    public bool Primary { get; set; }

    [Description("If true, adds a NOT NULL constraint.")]
    public bool NotNull { get; set; }

    [Description("If true, creates a secondary index on this column.")]
    public bool Index { get; set; }

    [Description("If true and Index is true, the secondary index is UNIQUE.")]
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
    private static readonly Regex StringTypeRegex = new(@"^string\(\d+\)$", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> ScalarTypes = new(StringComparer.OrdinalIgnoreCase)
        { "oid", "object_id", "int64", "float64", "float32", "bool", "bytes", "date", "datetime", "uuid" };

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
        IReadOnlyList<CamusBranchRow> branches = await camus.ShowBranchesAsync(database, ct).ConfigureAwait(false);
        Dictionary<string, object?>[] rows = branches.Select(BranchToRow).ToArray();
        return new ListBranchesResult(rows);
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

    // -------------------------------------------------------------------------
    // Query tools
    // -------------------------------------------------------------------------

    /// <summary>Executes a read-only SELECT or SHOW statement.</summary>
    [McpServerTool]
    [Description("Execute a SELECT or SHOW statement. Mutating statements (INSERT, UPDATE, DELETE, DROP, CREATE) are rejected before execution. Results are capped at max_rows (default 1000).")]
    public static async Task<SelectQueryResult> SelectQuery(
        CamusClient camus,
        McpConfig config,
        [Description("Database name (required for table queries; may be empty for SHOW DATABASES).")] string? database,
        [Description("SQL SELECT or SHOW statement to execute.")] string sql,
        [Description("Named parameters referenced in the SQL with @name placeholders. Values are JSON scalars.")] Dictionary<string, JsonElement>? parameters = null,
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
    [Description("Execute an EXPLAIN statement. Mode can be 'plan' (default), 'logical', or 'physical'. If the sql argument already starts with EXPLAIN, it is forwarded as-is; otherwise EXPLAIN is prepended. EXPLAIN ANALYZE on joins is unsupported and surfaces as a tool error.")]
    public static async Task<ExplainQueryResult> ExplainQuery(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("SQL to explain (SELECT statement or full EXPLAIN statement).")] string sql,
        [Description("Plan mode: 'plan' (default), 'logical', or 'physical'.")] string? mode = null,
        CancellationToken ct = default)
    {
        string keyword = StatementGuard.AssertExplain(sql);

        string explainSql;
        if (keyword == "EXPLAIN")
        {
            explainSql = sql;
        }
        else
        {
            // CamusDB grammar: EXPLAIN (LOGICAL) / EXPLAIN (PHYSICAL) / EXPLAIN
            string prefix = mode?.ToLowerInvariant() switch
            {
                "logical" => "EXPLAIN (LOGICAL)",
                "physical" => "EXPLAIN (PHYSICAL)",
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
    [Description("Create a new table. Columns are specified as a typed list; identifiers are validated before SQL composition. Accepted types: oid, int64, float64, float32, string(N), bool, bytes, date, datetime, uuid.")]
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
                else if (col.Index) sb.Append(" INDEX");
            }

            if (col.Default is not null)
            {
                sb.Append(" DEFAULT(");
                sb.Append(col.Default);
                sb.Append(')');
            }
        }

        sb.Append(')');

        await camus.ExecuteNonQueryAsync(database, sb.ToString(), null, ct).ConfigureAwait(false);
        return new CreateTableResult(Ok: true);
    }

    /// <summary>Inserts rows into a table using parameterized INSERT statements.</summary>
    [McpServerTool]
    [Description("Insert one or more rows into a table. Rows are a list of column→value objects. Values are passed as SQL parameters (no string interpolation). Large batches are chunked to respect the per-transaction mutation limit. Returns the total number of inserted rows.")]
    public static async Task<InsertRowsResult> InsertRows(
        CamusClient camus,
        [Description("Database name.")] string database,
        [Description("Table name.")] string table,
        [Description("Rows to insert. Each row is an object mapping column names to values (string, number, bool, or null).")] IReadOnlyList<Dictionary<string, JsonElement>> rows,
        CancellationToken ct = default)
    {
        IdentifierValidator.Validate(database, "database");
        IdentifierValidator.Validate(table, "table");

        if (rows == null || rows.Count == 0)
            return new InsertRowsResult(0);

        // Use first row to get column names; validate all column names.
        string[] columns = [.. rows[0].Keys];
        foreach (string col in columns)
            IdentifierValidator.Validate(col, "column name");

        int totalInserted = 0;

        // Chunk to stay within MaxMutationsPerTransaction. 500 rows per batch is safe for
        // tables with up to 40 secondary indexes (500 * 40 = 20,000 = MaxMutationsPerTransaction).
        const int ChunkSize = 500;
        foreach (IEnumerable<Dictionary<string, JsonElement>> chunk in Chunk(rows, ChunkSize))
        {
            List<Dictionary<string, JsonElement>> rowList = chunk.ToList();
            string sql = BuildInsertSql(table, columns, rowList);
            Dictionary<string, JsonElement> parameters = BuildInsertParameters(columns, rowList);
            totalInserted += await camus.ExecuteNonQueryAsync(database, sql, parameters, ct).ConfigureAwait(false);
        }

        return new InsertRowsResult(totalInserted);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string BuildInsertSql(string table, string[] columns, List<Dictionary<string, JsonElement>> rows)
    {
        StringBuilder sb = new();
        sb.Append("INSERT INTO ");
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

    private static void ValidateSqlType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new CamusException("CADB0400", "Column type must not be empty.");
        if (ScalarTypes.Contains(type) || StringTypeRegex.IsMatch(type))
            return;
        throw new CamusException("CADB0400",
            $"Unknown column type '{type}'. Accepted: oid, int64, float64, float32, string(N), bool, bytes, date, datetime, uuid.");
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

    private static Dictionary<string, object?> BranchToRow(CamusBranchRow branch) => new()
    {
        ["database"] = branch.Database,
        ["id"] = branch.Id,
        ["depth"] = branch.Depth,
        ["parent"] = branch.Parent,
        ["forkTimestamp"] = branch.ForkTimestamp,
    };
}
