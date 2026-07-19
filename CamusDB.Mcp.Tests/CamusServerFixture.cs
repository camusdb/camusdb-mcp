
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Mcp.Client;
using CamusDB.Mcp.Config;
using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// Shared fixture that connects a <see cref="CamusClient"/> to a live CamusDB instance for the
/// integration tests. The endpoint is read from <c>CAMUS_MCP_ENDPOINT</c> (defaulting to the
/// REST/JSON port used by the CI Docker container and the local dev server), and the target
/// database from <c>CAMUS_MCP_DEFAULT_DATABASE</c> (default <c>test</c>).
///
/// Reachability is probed once at startup with a context-free <c>SHOW DATABASES</c>. When the
/// server is not running (e.g. a plain <c>dotnet test</c> on a dev box with no CamusDB), the
/// integration tests skip themselves via <see cref="Available"/> rather than failing, so the
/// pure-logic unit tests still run green. CI always has a server, so there the tests execute.
/// </summary>
public sealed class CamusServerFixture : IAsyncLifetime
{
    public CamusClient Client { get; private set; } = default!;

    public McpConfig Config { get; private set; } = default!;

    public string Database { get; private set; } = "test";

    /// <summary>True when the CamusDB server responded to the startup probe.</summary>
    public bool Available { get; private set; }

    /// <summary>Human-readable reason the server was unreachable (used in the skip message).</summary>
    public string UnavailableReason { get; private set; } = "";

    /// <summary>
    /// When <c>CAMUS_MCP_REQUIRE_SERVER</c> is set (as in CI), an unreachable server is a hard
    /// failure instead of a skip — so a CamusDB that never came up cannot make the integration
    /// tests silently pass as "skipped".
    /// </summary>
    private static bool ServerRequired =>
        Environment.GetEnvironmentVariable("CAMUS_MCP_REQUIRE_SERVER") is { Length: > 0 };

    public async Task InitializeAsync()
    {
        string endpoint = Environment.GetEnvironmentVariable("CAMUS_MCP_ENDPOINT") ?? "http://localhost:5095";
        Database = Environment.GetEnvironmentVariable("CAMUS_MCP_DEFAULT_DATABASE") ?? "test";

        Config = new McpConfig
        {
            BaseConnectionString = $"Endpoint={endpoint}",
            DefaultDatabase = Database,
        };
        Client = new CamusClient(Config);

        try
        {
            // Context-free read; also confirms the client can open a connection to the endpoint.
            await Client.QueryAsync(null, "SHOW DATABASES", null, 0, CancellationToken.None);
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = $"CamusDB unreachable at {endpoint}: {ex.Message}";
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Skips the calling test when no CamusDB server is reachable — unless the server is required
    /// (CI), in which case it fails loudly.
    /// </summary>
    public void SkipIfUnavailable()
    {
        if (Available)
            return;

        if (ServerRequired)
            Assert.Fail(UnavailableReason);

        Skip.If(true, UnavailableReason);
    }
}
