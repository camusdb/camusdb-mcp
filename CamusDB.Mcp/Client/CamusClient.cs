
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
/// point for CREATE DATABASE.
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

    // -------------------------------------------------------------------------
    // Row / parameter marshalling
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> ReadRow(CamusDataReader reader)
    {
        Dictionary<string, object?> row = new(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = ReadCell(reader, i);
        return row;
    }

    /// <summary>
    /// Reads one cell as a JSON-friendly CLR value. Every type takes the client's own conversion
    /// except NUMERIC: the client reads it as a <see cref="decimal"/>, which holds 28-29 significant
    /// digits against NUMERIC's 38 and throws on a wider value. A NUMERIC is returned as its exact
    /// canonical text instead (<c>"1.5"</c>, <c>"1200"</c>), alone or as an array element, so a
    /// select never fails on a wide value and no digit goes through a double on the way to JSON.
    /// </summary>
    private static object? ReadCell(CamusDataReader reader, int ordinal)
    {
        ColumnValue cell = reader.GetColumnValue(ordinal);
        return cell.Type switch
        {
            ColumnType.Null => null,
            ColumnType.Numeric => cell.StrValue,
            ColumnType.Array when cell.ArrayElementType == ColumnType.Numeric =>
                cell.ArrayValues?.Select(e => e.Type == ColumnType.Null ? null : e.StrValue).ToArray() ?? [],
            _ => reader.GetValue(ordinal),
        };
    }

    private static void AddParameters(CamusCommand command, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null)
            return;

        foreach ((string key, JsonElement element) in parameters)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                (ColumnType elementType, object?[] values) = JsonArrayToColumn(key, element);
                command.Parameters.Add(key, elementType, values, isArray: true);
                continue;
            }

            (ColumnType type, object? value) = JsonElementToColumn(element);
            command.Parameters.Add(key, type, value);
        }
    }

    /// <summary>
    /// Maps a JSON scalar to a CamusDB (<see cref="ColumnType"/>, value) pair. Strings map to
    /// <see cref="ColumnType.String"/>, booleans to <see cref="ColumnType.Bool"/>, and
    /// null/anything else to <see cref="ColumnType.Null"/>.
    ///
    /// <para>An integer that fits a long maps to <see cref="ColumnType.Integer64"/>. Any other number
    /// maps to <see cref="ColumnType.Float64"/> when a double keeps every digit of its JSON text, and
    /// otherwise to <see cref="ColumnType.Numeric"/> with that exact text — so a 30-digit integer or
    /// a NUMERIC value such as <c>12345678901234567890.123456789</c> binds without rounding. The
    /// server coerces NUMERIC to a float column, and Integer64/Float64 to a NUMERIC column, on
    /// write.</para>
    /// </summary>
    public static (ColumnType Type, object? Value) JsonElementToColumn(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => (ColumnType.String, element.GetString()),
        JsonValueKind.True => (ColumnType.Bool, (object?)true),
        JsonValueKind.False => (ColumnType.Bool, (object?)false),
        JsonValueKind.Number => NumberToColumn(element),
        _ => (ColumnType.Null, null),
    };

    /// <summary>
    /// Maps a JSON array to an array parameter: the element type and the element values. Every
    /// non-null element must be a scalar of one JSON kind (all strings, all booleans or all numbers).
    /// Numbers take <see cref="ColumnType.Integer64"/> when each one is an integer, else
    /// <see cref="ColumnType.Float64"/>. An empty array is sent with no element type, and the server
    /// adopts the target column's element type. An all-null array is sent as a string array, because
    /// the client cannot bind non-empty elements without a type; a null element stays null in any
    /// target column.
    /// </summary>
    public static (ColumnType ElementType, object?[] Values) JsonArrayToColumn(string name, JsonElement array)
    {
        List<JsonElement> elements = [.. array.EnumerateArray()];
        object?[] values = new object?[elements.Count];

        JsonValueKind kind = JsonValueKind.Undefined;
        foreach (JsonElement element in elements)
        {
            if (element.ValueKind == JsonValueKind.Null)
                continue;

            JsonValueKind elementKind = element.ValueKind == JsonValueKind.False ? JsonValueKind.True : element.ValueKind;
            if (elementKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.Number))
                throw new CamusException("CADB0400", $"Array parameter '{name}' may only hold strings, numbers, booleans or nulls.");
            if (kind != JsonValueKind.Undefined && kind != elementKind)
                throw new CamusException("CADB0400", $"Array parameter '{name}' mixes element kinds; every element must have the same type.");
            kind = elementKind;
        }

        bool allIntegers = elements.All(e => e.ValueKind != JsonValueKind.Number || e.TryGetInt64(out _));
        ColumnType elementType = kind switch
        {
            JsonValueKind.String => ColumnType.String,
            JsonValueKind.True => ColumnType.Bool,
            JsonValueKind.Number => allIntegers ? ColumnType.Integer64 : ColumnType.Float64,
            _ => elements.Count == 0 ? ColumnType.Null : ColumnType.String,
        };

        for (int i = 0; i < elements.Count; i++)
        {
            JsonElement element = elements[i];
            values[i] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when elementType == ColumnType.Integer64 => element.GetInt64(),
                JsonValueKind.Number => element.GetDouble(),
                _ => null,
            };
        }

        return (elementType, values);
    }

    private static (ColumnType Type, object? Value) NumberToColumn(JsonElement element)
    {
        if (element.TryGetInt64(out long l))
            return (ColumnType.Integer64, l);

        string text = element.GetRawText();
        if (element.TryGetDouble(out double d) && double.IsFinite(d) && SignificantDigits(text) <= 15)
            return (ColumnType.Float64, d);

        return (ColumnType.Numeric, text);
    }

    /// <summary>
    /// Counts the significant digits of a JSON number's mantissa: leading zeros and the exponent are
    /// not counted. A double keeps any decimal number of up to 15 significant digits exactly.
    /// </summary>
    private static int SignificantDigits(string text)
    {
        int exponent = text.IndexOfAny(['e', 'E']);
        ReadOnlySpan<char> mantissa = exponent < 0 ? text : text.AsSpan(0, exponent);

        int count = 0;
        bool leading = true;
        int trailingZeros = 0;
        foreach (char c in mantissa)
        {
            if (c is < '0' or > '9')
                continue;
            if (leading && c == '0')
                continue;
            leading = false;
            count++;
            trailingZeros = c == '0' ? trailingZeros + 1 : 0;
        }

        return count - trailingZeros;
    }
}
