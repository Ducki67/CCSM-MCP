using System.Reflection;
using System.Text;

namespace ClaudeSessionManagerMCP.Config;

/// <summary>Runtime settings loaded from config.ini next to the exe.</summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 8779;
    public string LogLevel { get; set; } = "info";          // quiet | info | debug
    public bool KeepPcOn { get; set; } = true;              // bKeepPCOn
    public bool KeepDisplayOn { get; set; } = false;        // bKeepDisplayOn
    public int HeavyUsagePercent { get; set; } = 80;
    public int CriticalUsagePercent { get; set; } = 92;
    public int IdleTimeoutSeconds { get; set; } = 120;

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.ini");

    /// <summary>Load config.ini; if it does not exist yet, write a fully commented default file first.</summary>
    public static AppConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            try { File.WriteAllText(path, DefaultIniText, new UTF8Encoding(false)); }
            catch { /* read-only dir etc. — run with defaults */ }
        }

        var cfg = new AppConfig();
        foreach (var (key, value) in ReadIni(path))
        {
            switch (key)
            {
                case "server.port": cfg.Port = ParseInt(value, cfg.Port, 1, 65535); break;
                case "server.loglevel": cfg.LogLevel = value.ToLowerInvariant(); break;
                case "sleep.bkeeppcon": cfg.KeepPcOn = ParseBool(value, cfg.KeepPcOn); break;
                case "sleep.bkeepdisplayon": cfg.KeepDisplayOn = ParseBool(value, cfg.KeepDisplayOn); break;
                case "usage.heavyusagepercent": cfg.HeavyUsagePercent = ParseInt(value, cfg.HeavyUsagePercent, 1, 100); break;
                case "usage.criticalusagepercent": cfg.CriticalUsagePercent = ParseInt(value, cfg.CriticalUsagePercent, 1, 100); break;
                case "usage.idletimeoutseconds": cfg.IdleTimeoutSeconds = ParseInt(value, cfg.IdleTimeoutSeconds, 10, 86400); break;
            }
        }
        if (cfg.CriticalUsagePercent < cfg.HeavyUsagePercent)
            cfg.CriticalUsagePercent = Math.Min(100, cfg.HeavyUsagePercent + 5);
        return cfg;
    }

    private static IEnumerable<(string key, string value)> ReadIni(string path)
    {
        if (!File.Exists(path)) yield break;
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = $"{section}.{line[..eq].Trim().ToLowerInvariant()}";
            var value = line[(eq + 1)..].Trim();
            // strip trailing inline comment
            var cIdx = value.IndexOf(';');
            if (cIdx >= 0) value = value[..cIdx].Trim();
            yield return (key, value);
        }
    }

    private static int ParseInt(string value, int fallback, int min, int max)
        => int.TryParse(value, out var n) ? Math.Clamp(n, min, max) : fallback;

    private static bool ParseBool(string value, bool fallback)
    {
        var v = value.ToLowerInvariant();
        return v switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private const string DefaultIniText =
        """
        ; ---------------------------------------------------------------
        ; ClaudeSessionManagerMCP configuration
        ; Read by both the MCP server and the hub (this folder).
        ; ---------------------------------------------------------------

        [server]
        ; TCP port the hub listens on. 127.0.0.1 only — never exposed to the network.
        port = 8779
        ; Logging verbosity for the hub: quiet | info | debug
        logLevel = info

        [sleep]
        ; bKeepPCOn = keep the PC awake while connected sessions report working=true
        ; (session_checkin). When every session is done/disconnected — or this is
        ; false — Windows is allowed to sleep again. Set false to fully disable.
        bKeepPCOn = true
        ; Also keep the DISPLAY on while the PC is kept awake.
        ; Default false: screen can turn off, machine stays awake (better overnight).
        bKeepDisplayOn = false

        [usage]
        ; A session at/above heavyUsagePercent gets a "save to memory + compact"
        ; directive on its next check-in.
        heavyUsagePercent = 80
        ; At/above criticalUsagePercent the directive becomes urgent (compaction
        ; is imminent — write a resume note first).
        criticalUsagePercent = 92
        ; A session that hasn't checked in for this long is shown as "stale".
        idleTimeoutSeconds = 120
        """;
}
