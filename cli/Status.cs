using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Fo4Cli;

static class Paths
{
    public static readonly string MyGames = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Fallout4");
    public static readonly string PapyrusLog = Path.Combine(MyGames, "Logs", "Script", "Papyrus.0.log");
    public static readonly string F4ExecLog = Path.Combine(MyGames, "F4SE", "F4Exec.log");
    public static readonly string F4seLog = Path.Combine(MyGames, "F4SE", "f4se.log");
    public static readonly string CustomIni = Path.Combine(MyGames, "Fallout4Custom.ini");
}

static class Status
{
    public static JsonObject Collect()
    {
        var o = new JsonObject();
        var procs = Process.GetProcessesByName("Fallout4");
        o["game_running"] = procs.Length > 0;
        if (procs.Length > 0) o["game_pid"] = procs[0].Id;

        try
        {
            var ping = Pipe.Ping();
            o["f4exec_connected"] = true;
            o["f4exec_version"] = ping["version"]?.DeepClone();
        }
        catch (Exception e)
        {
            o["f4exec_connected"] = false;
            o["f4exec_error"] = e.Message;
        }

        o["papyrus_logging_enabled"] = PapyrusLoggingEnabled();
        o["papyrus_log"] = Paths.PapyrusLog;
        o["papyrus_log_exists"] = File.Exists(Paths.PapyrusLog);
        o["f4exec_log"] = Paths.F4ExecLog;
        return o;
    }

    public static bool PapyrusLoggingEnabled()
    {
        if (!File.Exists(Paths.CustomIni)) return false;
        string? section = null;
        foreach (var raw in File.ReadAllLines(Paths.CustomIni))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line; continue; }
            if (section?.Equals("[Papyrus]", StringComparison.OrdinalIgnoreCase) == true &&
                line.Replace(" ", "").Equals("bEnableLogging=1", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Explain why the pipe isn't there, using the logs F4SE and F4Exec leave behind.
    public static string Diagnose()
    {
        if (Process.GetProcessesByName("Fallout4").Length == 0)
            return "Fallout 4 is not running (launch it through f4se_loader.exe)";
        if (!File.Exists(Paths.F4ExecLog) || File.GetLastWriteTime(Paths.F4ExecLog) <
            Process.GetProcessesByName("Fallout4")[0].StartTime)
        {
            var hint = "";
            if (File.Exists(Paths.F4seLog))
            {
                var line = File.ReadLines(Paths.F4seLog).FirstOrDefault(l => l.Contains("F4Exec", StringComparison.OrdinalIgnoreCase));
                if (line != null) hint = $" f4se.log: {line.Trim()}";
            }
            return "F4Exec did not load this session - was the game started via f4se_loader.exe?" + hint;
        }
        var last = File.ReadLines(Paths.F4ExecLog).LastOrDefault() ?? "";
        return $"F4Exec pipe not reachable. F4Exec.log: {last}";
    }
}
