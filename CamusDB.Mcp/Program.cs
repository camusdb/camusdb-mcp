
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Mcp.Client;
using CamusDB.Mcp.Config;
using CamusDB.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

McpConfig config = McpConfig.FromEnvironment();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Suppress noisy host logs — MCP uses stdio; extra output on stdout breaks the protocol.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register config and the CamusDB.Client-backed adapter so tools can inject them.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<CamusClient>();

// MCP server over stdio.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CamusDbTools>();

await builder.Build().RunAsync();
