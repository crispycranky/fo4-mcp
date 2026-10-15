using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fo4Cli;

// Minimal MCP server over stdio (newline-delimited JSON-RPC 2.0).
static class Mcp
{
    static readonly JsonArray Tools =
    [
        Tool("fo4_exec",
            "Run Fallout 4 console commands in the running game (on its main thread) and return the console output of each. " +
            "Same syntax as the ~ console: e.g. 'player.getav CA_Raiders_Kills', 'cf \"0001A4D7\".setstage 10', 'prid 14', 'bat foo'. " +
            "Commands run in order; a selected reference (prid/click) persists between calls like in the real console.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["commands"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Console command lines to run in order"
                    }
                },
                ["required"] = new JsonArray("commands")
            }),
        Tool("fo4_logs",
            "Read the Papyrus script log (Papyrus.0.log). Without 'cursor' returns the last 'tail' lines; " +
            "pass the returned cursor back to get only lines written since then.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "Lines to return when no cursor (default 50)" },
                    ["cursor"] = new JsonObject { ["type"] = "integer", ["description"] = "Byte offset from a previous fo4_logs call" },
                    ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Case-insensitive regex; only matching lines" }
                }
            }),
        Tool("fo4_screenshot",
            "Capture the Fallout 4 window and return the image (plus the saved file path). Works when the game runs " +
            "borderless or windowed. Frame shots first with console commands via fo4_exec (tfc, setangle, moveto). " +
            "'hide_hud' toggles the HUD off for the capture (console 'tm').",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["hide_hud"] = new JsonObject { ["type"] = "boolean", ["description"] = "Hide the HUD for the shot (default false)" },
                    ["max_width"] = new JsonObject { ["type"] = "integer", ["description"] = "Downscale to this width (default 1280; 0 = full size)" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Where to save (default Pictures\\fo4-cli\\fo4_<time>.jpg)" }
                }
            }),
        Tool("fo4_status",
            "Check whether Fallout 4 is running, the F4Exec pipe is reachable, and Papyrus logging is enabled.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
    ];

    static JsonObject Tool(string name, string description, JsonObject schema) =>
        new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };

    public static int Run()
    {
        var stdout = Console.OpenStandardOutput();
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.TrimStart('﻿');
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line)?.AsObject(); }
            catch (JsonException) { continue; }
            if (msg == null) continue;

            var id = msg["id"]?.DeepClone();
            var method = msg["method"]?.GetValue<string>();
            if (id == null) continue; // notification

            JsonObject resp;
            try
            {
                resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(method, msg["params"] as JsonObject) };
            }
            catch (Exception e)
            {
                resp = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message }
                };
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(resp.ToJsonString(Json.Compact) + "\n");
            stdout.Write(bytes);
            stdout.Flush();
        }
        return 0;
    }

    static JsonNode Handle(string? method, JsonObject? p) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = p?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "fo4", ["version"] = "1.0.0" }
        },
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject { ["tools"] = Tools.DeepClone() },
        "tools/call" => CallTool(p?["name"]?.GetValue<string>(), p?["arguments"] as JsonObject ?? new JsonObject()),
        _ => throw new InvalidOperationException($"method not found: {method}")
    };

    static JsonObject CallTool(string? name, JsonObject args)
    {
        try
        {
            if (name == "fo4_screenshot") return ScreenshotResult(args);
            string text = name switch
            {
                "fo4_exec" => ExecText(args),
                "fo4_logs" => LogsText(args),
                "fo4_status" => Status.Collect().ToJsonString(Json.Pretty),
                _ => throw new InvalidOperationException($"unknown tool {name}")
            };
            return Result(text, false);
        }
        catch (Exception e)
        {
            return Result(e.Message, true);
        }
    }

    static JsonObject ScreenshotResult(JsonObject args)
    {
        var shot = Screenshot.Take(args["path"]?.GetValue<string>(), args["max_width"]?.GetValue<int>() ?? 1280,
                                   args["hide_hud"]?.GetValue<bool>() ?? false, jpeg: true);
        var text = $"saved {shot.Path} ({shot.Width}x{shot.Height})" + (shot.Black ? "\nwarning: " + Screenshot.BlackHint : "");
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.Mime },
                new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = shot.Black
        };
    }

    static JsonObject Result(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    static string ExecText(JsonObject args)
    {
        var cmds = args["commands"] switch
        {
            JsonArray a => a.Select(n => n!.GetValue<string>()).ToList(),
            JsonValue v => [v.GetValue<string>()],
            _ => throw new ArgumentException("commands is required")
        };
        var resp = Pipe.Exec(cmds);
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(resp["error"]?.GetValue<string>() ?? "exec failed");
        return Program.FormatResults(resp);
    }

    static string LogsText(JsonObject args)
    {
        int tail = args["tail"]?.GetValue<int>() ?? 50;
        long? cursor = args["cursor"]?.GetValue<long>();
        var f = args["filter"]?.GetValue<string>();
        var filter = string.IsNullOrEmpty(f) ? null : new Regex(f, RegexOptions.IgnoreCase);
        if (!File.Exists(Paths.PapyrusLog))
            return Status.PapyrusLoggingEnabled()
                ? $"{Paths.PapyrusLog} does not exist yet (starts when the game loads)"
                : "Papyrus logging is disabled. Run `fo4-cli logs --enable` and restart the game.";
        var (lines, next) = Logs.Read(Paths.PapyrusLog, cursor, tail, filter);
        return $"cursor: {next}\n" + string.Join("\n", lines);
    }
}
