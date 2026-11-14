namespace ClaudeSessionManagerMCP.Hub;

/// <summary>Hub logging: stderr (harmless) + optional hub.log file. Levels: quiet &lt; info &lt; debug.</summary>
public static class HubLog
{
    private static int _level = 1; // 0 quiet, 1 info, 2 debug
    private static readonly object FileLock = new();
    private static string? _filePath;
    private static long _maxBytes = 2_000_000; // simple 2MB cap; roll to .old when exceeded

    public static void Configure(Config.AppConfig config)
    {
        _level = config.LogLevel switch
        {
            "quiet" => 0,
            "debug" => 2,
            _ => 1,
        };
        try
        {
            _filePath = Path.Combine(AppContext.BaseDirectory, "hub.log");
        }
        catch { _filePath = null; }
    }

    public static int Level => _level;

    public static void Info(string message) => Write(1, message);
    public static void Debug(string message) => Write(2, message);
    public static void Warn(string message) => Write(1, "WARN " + message);
    public static void Error(string message) => Write(1, "ERROR " + message);

    private static void Write(int level, string message)
    {
        if (level > _level) return;
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}";

        try { Console.Error.WriteLine(line); } catch { }

        if (_filePath == null) return;
        try
        {
            lock (FileLock)
            {
                var fi = new FileInfo(_filePath);
                if (fi.Exists && fi.Length > _maxBytes)
                {
                    var old = _filePath + ".old";
                    File.Copy(_filePath, old, overwrite: true);
                    File.Delete(_filePath);
                }
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
