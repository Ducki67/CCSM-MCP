using ClaudeSessionManagerMCP.Config;
using ClaudeSessionManagerMCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeSessionManagerMCP.Services;

/// <summary>Assembles the MCP stdio server (default exe mode).</summary>
public static class McpHost
{
    public static async Task RunAsync(AppConfig config)
    {
        var builder = Host.CreateApplicationBuilder();

        // All logging to stderr; stdout is reserved for the MCP protocol.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<HubClient>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
