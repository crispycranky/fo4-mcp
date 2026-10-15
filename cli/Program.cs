using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fo4Cli;

static class Json
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

static class Program
{
    const string Usage = """
        fo4-cli - run Fallout 4 console commands through the F4Exec F4SE plugin

        usage:
          fo4-cli exec <command...> [--json]     run one console line, e.g. fo4-cli exec player.getav CA_Raiders_Kills
          fo4-cli run <file> [--json]            run each line of a file (like bat; ';' lines are comments)
          fo4-cli logs [-f] [-n N] [--grep RE]   show Papyrus.0.log (-f follows it live)
          fo4-cli logs --enable                  turn on Papyrus logging in Fallout4Custom.ini
          fo4-cli status [--json]                game / plugin / logging status
          fo4-cli key <enter|esc|tilde|...>      press keys in the game window (e.g. dismiss a popup)
          fo4-cli screenshot [-o FILE] [--width N] [--nohud] [--jpg]
                                                 capture the game window (borderless/windowed)
          fo4-cli mcp                            run as an MCP server on stdio
        """;

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var rest = args.Skip(1).ToList();
        bool json = rest.Remove("--json");
        try
        {
            return args[0] switch
            {
                "exec" => Exec([string.Join(' ', rest)], json),
                "run" => Exec(File.ReadAllLines(rest.Single()), json),
                "logs" => LogsCmd(rest),
                "status" => StatusCmd(json),
                "mcp" => Mcp.Run(),
                "key" => Keys.Press(rest),
                "screenshot" => Screenshot.Cmd(rest),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}")
            };
        }
        catch (F4ExecUnavailable e) { return Fail(e.Message, 2); }
        catch (Exception e) { return Fail(e.Message); }
    }

    static int Fail(string msg, int code = 1)
    {
        Console.Error.WriteLine("error: " + msg);
        return code;
    }

    static int Exec(IEnumerable<string> lines, bool json)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (list.Count == 0 || (list.Count == 1 && list[0].Trim().Length == 0)) return Fail("no command given");
        var resp = Pipe.Exec(list);
        if (json)
        {
            Console.WriteLine(resp.ToJsonString(Json.Pretty));
            return resp["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        if (resp["ok"]?.GetValue<bool>() != true)
            return Fail(resp["error"]?.GetValue<string>() ?? "exec failed");
        Console.Write(FormatResults(resp, list.Count > 1));
        return 0;
    }

    public static string FormatResults(JsonObject resp, bool showCommands = true)
    {
        var sb = new StringBuilder();
        foreach (var r in resp["results"]!.AsArray())
        {
            var cmd = r!["command"]!.GetValue<string>();
            var output = r["output"]!.GetValue<string>();
            if (showCommands) sb.Append("> ").Append(cmd).Append('\n');
            if (r["exception"]?.GetValue<bool>() == true) sb.Append("(game raised an exception running this command)\n");
            sb.Append(output.Length == 0 ? "(no output)\n" : output.EndsWith('\n') ? output : output + "\n");
        }
        return sb.ToString();
    }

    static int LogsCmd(List<string> a)
    {
        if (a.Remove("--enable"))
        {
            Logs.EnablePapyrusLogging();
            Console.WriteLine($"Papyrus logging enabled in {Paths.CustomIni} (takes effect next game launch)");
            return 0;
        }
        bool follow = a.Remove("-f") | a.Remove("--follow");
        int n = 40;
        Regex? filter = null;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] is "-n" or "--lines") n = int.Parse(a[++i]);
            else if (a[i] is "--grep") filter = new Regex(a[++i], RegexOptions.IgnoreCase);
            else return Fail($"unknown logs option '{a[i]}'");
        }
        if (!Status.PapyrusLoggingEnabled())
            Console.Error.WriteLine("warning: Papyrus logging is off; run `fo4-cli logs --enable` and restart the game");

        if (follow)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Logs.Follow(Paths.PapyrusLog, n, filter, cts.Token);
            return 0;
        }
        if (!File.Exists(Paths.PapyrusLog)) return Fail($"{Paths.PapyrusLog} not found");
        foreach (var l in Logs.Read(Paths.PapyrusLog, null, n, filter).Lines) Console.WriteLine(l);
        return 0;
    }

    static int StatusCmd(bool json)
    {
        var s = Status.Collect();
        if (json) { Console.WriteLine(s.ToJsonString(Json.Pretty)); return 0; }
        foreach (var (k, v) in s) Console.WriteLine($"{k,-24} {v}");
        return s["f4exec_connected"]?.GetValue<bool>() == true ? 0 : 2;
    }
}
