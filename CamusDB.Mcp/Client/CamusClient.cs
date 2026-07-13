
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text.Json;
using CamusDB.Client;
using CamusDB.Mcp.Config;

namespace CamusDB.Mcp.Client;

/// <summary>
/// Result of a read query: the materialized rows plus whether more rows existed beyond the
/// requested cap. Each row is a column-name → CLR-value map read out of the
/// <see cref="CamusDataReader"/> column by column.
/// </summary>
public sealed record QueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated);

/// <summary>
/// Thin adapter over <c>CamusDB.Client</c>. Each call opens a short-lived
/// <see cref="CamusConnection"/> scoped to the requested database (autocommit — no transaction
/// handle is carried between calls) and issues the statement through the real client API:
/// reader queries for reads, non-query execution for DML/DDL, and the dedicated client entry
/// points for CREATE DATABASE and SHOW BRANCHES.
///
/// This replaces the previous hand-rolled REST client; it never issues raw HTTP itself.
/// </summary>
public sealed class CamusClient
{
    private readonly McpConfig _config;

    public CamusClient(McpConfig config) => _config = config;

    private async Task<CamusConnection> OpenAsync(string? database, CancellationToken ct)
    {
        CamusConnection connection = new(_config.BuildConnectionString(database));
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Executes a read statement (SELECT / SHOW / EXPLAIN family) and materializes up to
    /// <paramref name="cap"/> rows. When <paramref name="cap"/> is non-positive every row is read.
    /// Truncation is detected by attempting to read one row past the cap.
    /// </summary>
    public async Task<QueryResult> QueryAsync(
        string? database,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        int cap,
        CancellationToken ct)
    {
        await using CamusConnection connection = await OpenAsync(database, ct).ConfigureAwait(false);
        using CamusCommand command = connection.CreateSelectCommand(sql);
        AddParameters(command, parameters);

        await using CamusDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<Dictionary<string, object?>> rows = [];
        bool truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (cap > 0 && rows.Count >= cap)
            {
                // One extra readable row beyond the cap means the result was truncated.
                truncated = true;
                break;
            }

            rows.Add(ReadRow(reader));
        }

        return new QueryResult(rows, truncated);
    }

    /// <summary>
    /// Executes a mutating statement (DML or DDL) and returns the affected row count. The client
    /// routes CREATE/DROP/ALTER TABLE and index DDL to the DDL endpoint automatically; INSERT/
    /// UPDATE/DELETE go to the non-query endpoint. Values are passed through the parameterized
    /// placeholder channel — never string-interpolated.
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(
        string? database,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        CancellationToken ct)
    {
        await using CamusConnection connection = await OpenAsync(database, ct).ConfigureAwait(false);
        using CamusCommand command = connection.CreateCamusCommand(sql);
        AddParameters(command, parameters);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Creates a database via the client's dedicated CREATE DATABASE entry point.</summary>
    public async Task CreateDatabaseAsync(string name, bool ifNotExists, CancellationToken ct)
    {
        await using CamusConnection connection = await OpenAsync(name, ct).ConfigureAwait(false);
        await connection.CreateDatabaseAsync(name, ifNotExists, ct).ConfigureAwait(false);
    }

    /// <summary>Lists the branches of <paramref name="database"/> via the client's SHOW BRANCHES entry point.</summary>
    public async Task<IReadOnlyList<CamusBranchRow>> ShowBranchesAsync(string database, CancellationToken ct)
    {
        await using CamusConnection connection = await OpenAsync(database, ct).ConfigureAwait(false);
        return await connection.ShowBranchesAsync(database, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Row / parameter marshalling
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> ReadRow(CamusDataReader reader)
    {
        Dictionary<string, object?> row = new(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static void AddParameters(CamusCommand command, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null)
            return;

        foreach ((string key, JsonElement element) in parameters)
        {
            (ColumnType type, object? value) = JsonElementToColumn(element);
            command.Parameters.Add(key, type, value);
        }
    }

    /// <summary>
    /// Maps a JSON scalar to a CamusDB (<see cref="ColumnType"/>, value) pair. Strings map to
    /// <see cref="ColumnType.String"/>, integers to <see cref="ColumnType.Integer64"/>, other
    /// numbers to <see cref="ColumnType.Float64"/>, booleans to <see cref="ColumnType.Bool"/>, and
    /// null/anything else to <see cref="ColumnType.Null"/>.
    /// </summary>
    public static (ColumnType Type, object? Value) JsonElementToColumn(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => (ColumnType.String, element.GetString()),
        JsonValueKind.True => (ColumnType.Bool, (object?)true),
        JsonValueKind.False => (ColumnType.Bool, (object?)false),
        JsonValueKind.Number => element.TryGetInt64(out long l)
            ? (ColumnType.Integer64, (object?)l)
            : (ColumnType.Float64, element.GetDouble()),
        _ => (ColumnType.Null, null),
    };
}
