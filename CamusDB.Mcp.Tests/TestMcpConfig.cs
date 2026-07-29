
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;
using CamusDB.Mcp.Config;
using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// Tests for <see cref="McpConfig"/>, focused on how credentials for an authenticated CamusDB
/// server reach the connection string. Configuration is resolved through the injectable lookup
/// overload of <c>FromEnvironment</c>, so these tests never mutate the process environment.
/// </summary>
public sealed class TestMcpConfig
{
    private static Func<string, string?> Env(params (string Key, string Value)[] entries)
    {
        Dictionary<string, string> map = entries.ToDictionary(e => e.Key, e => e.Value);
        return key => map.TryGetValue(key, out string? value) ? value : null;
    }

    // -------------------------------------------------------------------------
    // No credentials configured — behaviour must be unchanged
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildConnectionString_WithoutCredentials_AddsNoAuthKeys()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(("CAMUS_MCP_ENDPOINT", "http://localhost:5095")));
        CamusConnectionStringBuilder builder = config.BuildConnectionString("test");

        Assert.False(builder.Config.ContainsKey("User"));
        Assert.False(builder.Config.ContainsKey("Password"));
        Assert.False(builder.Config.ContainsKey("AccessToken"));
        Assert.False(builder.Config.ContainsKey("TokenLifetime"));
        Assert.Equal("test", builder.Config["Database"]);
    }

    [Fact]
    public void BuildConnectionString_KeepsCredentialsFromTheBaseConnectionString()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_CONNECTION_STRING", "Endpoint=http://localhost:5095;Database=test;User=app;Password=secret")));

        CamusConnectionStringBuilder builder = config.BuildConnectionString(null);

        Assert.Equal("app", builder.Config["User"]);
        Assert.Equal("secret", builder.Config["Password"]);
        Assert.Equal("test", builder.Config["Database"]);
    }

    // -------------------------------------------------------------------------
    // Credentials from dedicated environment variables
    // -------------------------------------------------------------------------

    [Fact]
    public void FromEnvironment_ReadsUserAndPassword()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_ENDPOINT", "http://localhost:5095"),
            ("CAMUS_MCP_USER", "app"),
            ("CAMUS_MCP_PASSWORD", "app-secret")));

        Assert.Equal("app", config.User);
        Assert.Equal("app-secret", config.Password);

        CamusConnectionStringBuilder builder = config.BuildConnectionString("test");
        Assert.Equal("app", builder.Config["User"]);
        Assert.Equal("app-secret", builder.Config["Password"]);
    }

    [Fact]
    public void FromEnvironment_ReadsAccessTokenAndTokenLifetime()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_ENDPOINT", "http://localhost:5095"),
            ("CAMUS_MCP_ACCESS_TOKEN", "camus_abc.def"),
            ("CAMUS_MCP_TOKEN_LIFETIME_SECONDS", "300")));

        Assert.Equal("camus_abc.def", config.AccessToken);
        Assert.Equal(300, config.TokenLifetimeSeconds);

        CamusConnectionStringBuilder builder = config.BuildConnectionString("test");
        Assert.Equal("camus_abc.def", builder.Config["AccessToken"]);
        Assert.Equal("300", builder.Config["TokenLifetime"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void FromEnvironment_IgnoresInvalidTokenLifetime(string value)
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_ENDPOINT", "http://localhost:5095"),
            ("CAMUS_MCP_TOKEN_LIFETIME_SECONDS", value)));

        Assert.Null(config.TokenLifetimeSeconds);
        Assert.False(config.BuildConnectionString("test").Config.ContainsKey("TokenLifetime"));
    }

    [Fact]
    public void FromEnvironment_IgnoresEmptyCredentials()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_ENDPOINT", "http://localhost:5095"),
            ("CAMUS_MCP_USER", ""),
            ("CAMUS_MCP_PASSWORD", "")));

        Assert.Null(config.User);
        Assert.Null(config.Password);
    }

    // -------------------------------------------------------------------------
    // Environment credentials override the connection string, aliases included
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildConnectionString_EnvironmentCredentialsOverrideConnectionString()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_CONNECTION_STRING", "Endpoint=http://localhost:5095;Database=test;User=old;Password=old-secret"),
            ("CAMUS_MCP_USER", "app"),
            ("CAMUS_MCP_PASSWORD", "app-secret")));

        CamusConnectionStringBuilder builder = config.BuildConnectionString(null);

        Assert.Equal("app", builder.Config["User"]);
        Assert.Equal("app-secret", builder.Config["Password"]);
    }

    [Theory]
    [InlineData("Uid")]
    [InlineData("UserId")]
    [InlineData("Username")]
    [InlineData("uid")]
    public void BuildConnectionString_ClearsUserAliases(string alias)
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_CONNECTION_STRING", $"Endpoint=http://localhost:5095;Database=test;{alias}=old"),
            ("CAMUS_MCP_USER", "app")));

        CamusConnectionStringBuilder builder = config.BuildConnectionString(null);

        Assert.Equal("app", builder.Config["User"]);
        Assert.DoesNotContain(builder.Config, kv =>
            string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kv.Key, "User", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildConnectionString_ClearsPasswordAlias()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_CONNECTION_STRING", "Endpoint=http://localhost:5095;Database=test;Pwd=old-secret"),
            ("CAMUS_MCP_PASSWORD", "app-secret")));

        CamusConnectionStringBuilder builder = config.BuildConnectionString(null);

        Assert.Equal("app-secret", builder.Config["Password"]);
        Assert.False(builder.Config.ContainsKey("Pwd"));
    }

    // -------------------------------------------------------------------------
    // Database resolution still wins over everything else
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildConnectionString_PerRequestDatabaseStillOverridesDefault()
    {
        McpConfig config = McpConfig.FromEnvironment(Env(
            ("CAMUS_MCP_CONNECTION_STRING", "Endpoint=http://localhost:5095;Database=test;User=app;Password=app-secret")));

        Assert.Equal("test", config.DefaultDatabase);
        Assert.Equal("other", config.BuildConnectionString("other").Config["Database"]);
        Assert.Equal("app", config.BuildConnectionString("other").Config["User"]);
    }
}
