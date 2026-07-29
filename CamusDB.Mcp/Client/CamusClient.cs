
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
///
/// Against a server with authentication enabled the credentials travel in the connection string
/// (see <see cref="McpConfig"/>) and the client handles the login/token exchange itself: one login
/// per credential set, cached process-wide, so the short-lived connections opened here do not each
/// pay for a password verification. Authentication failures are re-thrown with the CamusDB error
/// code and an actionable hint, since a bare "authentication failed" invites a pointless retry.
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
    /// Runs <paramref name="action"/>, re-throwing an authentication/authorization
    /// <see cref="CamusException"/> with its code and a remediation hint folded into the message.
    /// The code is preserved so callers can still branch on it; every other exception passes
    /// through untouched.
    /// </summary>
    private static async Task<T> WithAuthDiagnostics<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CamusException ex) when (AuthHint(ex.Code) is { } hint)
        {
            throw new CamusException(ex.Code, $"{ex.Message} [{ex.Code}] {hint}");
        }
    }

    /// <summary>
    /// Remediation hint for the authentication error codes, or null for any other code (which is
    /// left alone). These failures are never fixed by retrying the tool call, so each hint says
    /// what has to change instead.
    /// </summary>
    private static string? AuthHint(string code) => code switch
    {
        "CADB0516" =>
            "The server requires authentication and the credentials were missing, wrong, or expired. " +
            "Configure CAMUS_MCP_USER and CAMUS_MCP_PASSWORD (or CAMUS_MCP_ACCESS_TOKEN) on the MCP " +
            "server process and restart it; a token passed via CAMUS_MCP_ACCESS_TOKEN is never renewed " +
            "and may simply have expired. Retrying this call will not help.",
        "CADB0517" =>
            "Authenticated, but the user lacks a privilege on a table the statement touches. " +
            "A superuser must run GRANT <privilege> ON <database>.* TO <user>. Retrying will not help.",
        "CADB0518" =>
            "Too many login attempts for this account (the server rate-limits logins per account per " +
            "minute). Wait before retrying.",
        "CADB0519" =>
            "Credentials were sent over a plaintext connection where the server requires TLS. Use an " +
            "https:// endpoint, or run the server with --require-tls-when-auth-enabled false when the " +
            "plaintext hop stays inside the trust boundary.",
        _ => null,
    };

    /// <summary>
    /// Executes a read statement (SELECT / SHOW / EXPLAIN family) and materializes up to
    /// <paramref name="cap"/> rows. When <paramref name="cap"/> is non-positive every row is read.
    /// Truncation is detected by attempting to read one row past the cap.
    /// </summary>
    public Task<QueryResult> QueryAsync(
        string? database,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        int cap,
        CancellationToken ct) => WithAuthDiagnostics(async () =>
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
    });

    /// <summary>
    /// Executes a mutating statement (DML or DDL) and returns the affected row count. The client
    /// routes CREATE/DROP/ALTER TABLE and index DDL to the DDL endpoint automatically; INSERT/
    /// UPDATE/DELETE go to the non-query endpoint. Values are passed through the parameterized
    /// placeholder channel — never string-interpolated.
    /// </summary>
    public Task<int> ExecuteNonQueryAsync(
        string? database,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        CancellationToken ct) => WithAuthDiagnostics(async () =>
    {
        await using CamusConnection connection = await OpenAsync(database, ct).ConfigureAwait(false);
        using CamusCommand command = connection.CreateCamusCommand(sql);
        AddParameters(command, parameters);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    });

    /// <summary>Creates a database via the client's dedicated CREATE DATABASE entry point.</summary>
    public Task CreateDatabaseAsync(string name, bool ifNotExists, CancellationToken ct) =>
        WithAuthDiagnostics<object?>(async () =>
        {
            await using CamusConnection connection = await OpenAsync(name, ct).ConfigureAwait(false);
            await connection.CreateDatabaseAsync(name, ifNotExists, ct).ConfigureAwait(false);
            return null;
        });

    /// <summary>Lists the branches of <paramref name="database"/> via the client's SHOW BRANCHES entry point.</summary>
    public Task<IReadOnlyList<CamusBranchRow>> ShowBranchesAsync(string database, CancellationToken ct) =>
        WithAuthDiagnostics(async () =>
        {
            await using CamusConnection connection = await OpenAsync(database, ct).ConfigureAwait(false);
            return await connection.ShowBranchesAsync(database, ct).ConfigureAwait(false);
        });

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
