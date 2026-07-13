
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;

namespace CamusDB.Mcp.Config;

/// <summary>
/// Configuration for the CamusDB MCP server process. All values are read from environment
/// variables at startup; no config file is required.
///
/// The server talks to CamusDB exclusively through the <c>CamusDB.Client</c> ADO.NET-style
/// provider, so configuration is expressed as the connection-string inputs that
/// <see cref="CamusConnectionStringBuilder"/> understands (<c>Endpoint</c>, <c>Database</c>,
/// <c>Timeout</c>). Either supply a full connection string via
/// <c>CAMUS_MCP_CONNECTION_STRING</c>, or supply <c>CAMUS_MCP_ENDPOINT</c> (plus optional
/// <c>CAMUS_MCP_DEFAULT_DATABASE</c>) and let the server assemble one.
/// </summary>
public sealed class McpConfig
{
    /// <summary>
    /// Base connection string used to reach CamusDB. Always carries at least an <c>Endpoint</c>.
    /// The <c>Database</c> entry is treated as a default and is overridden per request by the
    /// database argument a tool receives (see <see cref="BuildConnectionString"/>).
    /// </summary>
    public string BaseConnectionString { get; set; } = "Endpoint=http://localhost:7141";

    /// <summary>Default database applied when a tool call omits one (may be null).</summary>
    public string? DefaultDatabase { get; set; }

    /// <summary>Hard cap on rows returned by <c>select_query</c>.</summary>
    public int MaxRows { get; set; } = 1000;

    public static McpConfig FromEnvironment()
    {
        McpConfig cfg = new();

        string? connectionString = Environment.GetEnvironmentVariable("CAMUS_MCP_CONNECTION_STRING");
        string? endpoint = Environment.GetEnvironmentVariable("CAMUS_MCP_ENDPOINT");
        string? defaultDatabase = Environment.GetEnvironmentVariable("CAMUS_MCP_DEFAULT_DATABASE");
        string? timeout = Environment.GetEnvironmentVariable("CAMUS_MCP_TIMEOUT_SECONDS");

        if (!string.IsNullOrWhiteSpace(defaultDatabase))
            cfg.DefaultDatabase = defaultDatabase;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            cfg.BaseConnectionString = connectionString;

            // Adopt the connection string's Database as the default when the caller did not set one
            // explicitly, so per-request database omission still resolves to something sensible.
            CamusConnectionStringBuilder probe = new(connectionString);
            if (cfg.DefaultDatabase is null &&
                probe.Config.TryGetValue("Database", out string? db) &&
                !string.IsNullOrWhiteSpace(db))
            {
                cfg.DefaultDatabase = db;
            }
        }
        else if (!string.IsNullOrWhiteSpace(endpoint))
        {
            cfg.BaseConnectionString = timeout is { Length: > 0 } && int.TryParse(timeout, out int seconds) && seconds > 0
                ? $"Endpoint={endpoint};Timeout={seconds}"
                : $"Endpoint={endpoint}";
        }

        if (Environment.GetEnvironmentVariable("CAMUS_MCP_MAX_ROWS") is { Length: > 0 } mr &&
            int.TryParse(mr, out int maxRows) && maxRows > 0)
        {
            cfg.MaxRows = maxRows;
        }

        return cfg;
    }

    /// <summary>
    /// Produces a <see cref="CamusConnectionStringBuilder"/> scoped to <paramref name="database"/>.
    /// The <c>Database</c> entry from the base connection string is replaced by
    /// <paramref name="database"/> (falling back to <see cref="DefaultDatabase"/>, then to an empty
    /// string). CamusDB context-free statements such as <c>SHOW DATABASES</c> tolerate an empty
    /// database; table-scoped statements require a real one.
    /// </summary>
    public CamusConnectionStringBuilder BuildConnectionString(string? database)
    {
        CamusConnectionStringBuilder builder = new(BaseConnectionString);
        builder.Config["Database"] = database ?? DefaultDatabase ?? "";
        return builder;
    }
}
