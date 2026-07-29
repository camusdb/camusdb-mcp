
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
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
///
/// Credentials for an authenticated CamusDB server (<c>CAMUSDB_AUTH_ENABLED=true</c>) are
/// configured the same way — either inline in <c>CAMUS_MCP_CONNECTION_STRING</c> or through the
/// dedicated <c>CAMUS_MCP_USER</c> / <c>CAMUS_MCP_PASSWORD</c> / <c>CAMUS_MCP_ACCESS_TOKEN</c>
/// variables, which keep the secret out of a connection string that also carries non-secret
/// settings. Authentication itself is entirely the client's job: it exchanges the password for a
/// short-lived bearer token once per credential set (cached process-wide, so the short-lived
/// per-call connections this server opens still perform a single login) and renews it
/// automatically.
/// </summary>
public sealed class McpConfig
{
    // Connection-string keys the client accepts for the same credential, including its aliases.
    // Setting one of them from the environment clears the others so a base connection string
    // cannot leave a stale alias behind (e.g. `Uid=old` alongside an environment `User=new`).
    private static readonly string[] UserKeys = ["User", "UserId", "Uid", "Username"];
    private static readonly string[] PasswordKeys = ["Password", "Pwd"];
    private static readonly string[] AccessTokenKeys = ["AccessToken"];
    private static readonly string[] TokenLifetimeKeys = ["TokenLifetime"];

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

    /// <summary>
    /// User to authenticate as against a server with authentication enabled (null when the server
    /// is unauthenticated, or when the credentials already live in the base connection string).
    /// </summary>
    public string? User { get; set; }

    /// <summary>That user's password. Exchanged by the client for a bearer token; never sent with a statement.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// A bearer token minted elsewhere, used verbatim instead of logging in. The client cannot
    /// renew it (it has no password to mint a replacement with), so an expired or revoked token
    /// surfaces as <c>CADB0516</c> rather than being retried.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Fallback token lifetime in seconds, used only when the server reports no expiry
    /// (client default: 600). Null leaves the client default in place.
    /// </summary>
    public int? TokenLifetimeSeconds { get; set; }

    public static McpConfig FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Testable core of <see cref="FromEnvironment()"/>: resolves every setting through
    /// <paramref name="lookup"/> instead of the process environment.
    /// </summary>
    public static McpConfig FromEnvironment(Func<string, string?> lookup)
    {
        McpConfig cfg = new();

        string? connectionString = lookup("CAMUS_MCP_CONNECTION_STRING");
        string? endpoint = lookup("CAMUS_MCP_ENDPOINT");
        string? defaultDatabase = lookup("CAMUS_MCP_DEFAULT_DATABASE");
        string? timeout = lookup("CAMUS_MCP_TIMEOUT_SECONDS");

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

        if (lookup("CAMUS_MCP_MAX_ROWS") is { Length: > 0 } mr &&
            int.TryParse(mr, out int maxRows) && maxRows > 0)
        {
            cfg.MaxRows = maxRows;
        }

        // Credentials. Kept out of BaseConnectionString so a secret supplied on its own does not
        // have to be pasted into a string that also carries endpoint/timeout settings; they are
        // merged in per request by BuildConnectionString.
        if (lookup("CAMUS_MCP_USER") is { Length: > 0 } user)
            cfg.User = user;

        if (lookup("CAMUS_MCP_PASSWORD") is { Length: > 0 } password)
            cfg.Password = password;

        if (lookup("CAMUS_MCP_ACCESS_TOKEN") is { Length: > 0 } accessToken)
            cfg.AccessToken = accessToken;

        if (lookup("CAMUS_MCP_TOKEN_LIFETIME_SECONDS") is { Length: > 0 } lifetime &&
            int.TryParse(lifetime, out int lifetimeSeconds) && lifetimeSeconds > 0)
        {
            cfg.TokenLifetimeSeconds = lifetimeSeconds;
        }

        return cfg;
    }

    /// <summary>
    /// Produces a <see cref="CamusConnectionStringBuilder"/> scoped to <paramref name="database"/>.
    /// The <c>Database</c> entry from the base connection string is replaced by
    /// <paramref name="database"/> (falling back to <see cref="DefaultDatabase"/>, then to an empty
    /// string). CamusDB context-free statements such as <c>SHOW DATABASES</c> tolerate an empty
    /// database; table-scoped statements require a real one.
    ///
    /// Credentials configured out of band (<see cref="User"/>, <see cref="Password"/>,
    /// <see cref="AccessToken"/>, <see cref="TokenLifetimeSeconds"/>) are merged in here, replacing
    /// whatever the base connection string carried for the same credential. Nothing is added when
    /// none are configured, so an unauthenticated server keeps behaving exactly as before.
    /// </summary>
    public CamusConnectionStringBuilder BuildConnectionString(string? database)
    {
        CamusConnectionStringBuilder builder = new(BaseConnectionString);
        builder.Config["Database"] = database ?? DefaultDatabase ?? "";

        SetCredential(builder, UserKeys, User);
        SetCredential(builder, PasswordKeys, Password);
        SetCredential(builder, AccessTokenKeys, AccessToken);
        SetCredential(builder, TokenLifetimeKeys, TokenLifetimeSeconds?.ToString(CultureInfo.InvariantCulture));

        return builder;
    }

    /// <summary>
    /// Writes <paramref name="value"/> under the canonical key of <paramref name="keys"/> (its
    /// first entry) and removes every alias, so the client sees exactly one spelling of the
    /// credential. A null <paramref name="value"/> leaves the connection string untouched — the
    /// credential was not configured out of band, so anything the base connection string carries
    /// stands.
    /// </summary>
    private static void SetCredential(CamusConnectionStringBuilder builder, string[] keys, string? value)
    {
        if (value is null)
            return;

        // Match aliases case-insensitively: the client keeps connection-string keys as the operator
        // spelled them, so `uid=` and `Uid=` are both possible.
        string[] stale = [.. builder.Config.Keys.Where(k =>
            k != keys[0] && keys.Any(alias => string.Equals(k, alias, StringComparison.OrdinalIgnoreCase)))];

        foreach (string alias in stale)
            builder.Config.Remove(alias);

        builder.Config[keys[0]] = value;
    }
}
