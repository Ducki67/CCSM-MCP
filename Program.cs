using ClaudeSessionManagerMCP.Config;
using ClaudeSessionManagerMCP.Hub;
using ClaudeSessionManagerMCP.Services;

// ---------------------------------------------------------------------------
// ClaudeSessionManagerMCP — one exe, two modes:
//
//   (no args)      MCP stdio server        (this is what Claude spawns)
//   hub            TCP hub process         (auto-spawned by the server on demand;
//                                          can also be started manually)
//   help           usage text
// ---------------------------------------------------------------------------

var argsList = args.Select(a => a.TrimStart('-', '/')).Select(a => a.ToLowerInvariant()).ToList();

if (argsList.Contains("help"))
{
    Console.WriteLine(
        """
        ClaudeSessionManagerMCP v1.0.1

        One exe, two modes:

          ClaudeSessionManagerMCP.exe            MCP stdio server (what Claude spawns)
          ClaudeSessionManagerMCP.exe hub        session coordination hub (TCP, auto-spawned)
          ClaudeSessionManagerMCP.exe help       this text

        Both modes read config.ini from the exe folder (port, bKeepPCOn, usage thresholds).
        """);
    return 0;
}

var config = AppConfig.Load();

// Optional overrides: hub --port N
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p))
        config.Port = Math.Clamp(p, 1, 65535);
}

if (argsList.Contains("hub"))
{
    HubLog.Configure(config);
    HubLog.Info($"Hub starting (port {config.Port}, keepPCOn={config.KeepPcOn})");
    using var hub = new HubServer(config);
    try
    {
        await hub.RunAsync();
    }
    catch (OperationCanceledException) { }
    finally
    {
        hub.ReleaseAwakeAndShutdown();
        HubLog.Info("Hub stopped.");
    }
    return 0;
}

// Default: MCP stdio server. All logging goes to stderr — stdout is the protocol.
await McpHost.RunAsync(config);
return 0;
