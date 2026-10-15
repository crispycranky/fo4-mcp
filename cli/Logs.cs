using System.Text;
using System.Text.RegularExpressions;

namespace Fo4Cli;

// Reads Papyrus.0.log while the game holds it open. The game recreates the file each
// session (old one rotates to Papyrus.1.log), so a shrinking file means start over.
static class Logs
{
    static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static (List<string> Lines, long Cursor) Read(string path, long? since, int tail, Regex? filter)
    {
        if (!File.Exists(path)) return ([], 0);
        using var fs = Open(path);
        long len = fs.Length;
        long start = since is long s && s <= len ? s : 0;
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, true);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (filter == null || filter.IsMatch(line)) lines.Add(line);
        if (since == null && lines.Count > tail) lines = lines.GetRange(lines.Count - tail, tail);
        return (lines, len);
    }

    public static void Follow(string path, int tail, Regex? filter, CancellationToken ct)
    {
        var (initial, cursor) = Read(path, null, tail, filter);
        foreach (var l in initial) Console.WriteLine(l);
        if (!File.Exists(path)) Console.Error.WriteLine($"(waiting for {path})");

        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(250);
            if (!File.Exists(path)) continue;
            using var fs = Open(path);
            if (fs.Length < cursor)
            {
                Console.Error.WriteLine("--- log restarted (new game session) ---");
                cursor = 0;
                pending.Clear();
            }
            if (fs.Length == cursor) continue;
            fs.Seek(cursor, SeekOrigin.Begin);
            var buf = new byte[fs.Length - cursor];
            int n = fs.Read(buf, 0, buf.Length);
            cursor += n;
            pending.Append(Encoding.UTF8.GetString(buf, 0, n));

            var text = pending.ToString();
            int lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) continue;
            foreach (var l in text[..lastNl].Split('\n'))
            {
                var line = l.TrimEnd('\r');
                if (filter == null || filter.IsMatch(line)) Console.WriteLine(line);
            }
            pending.Clear().Append(text[(lastNl + 1)..]);
        }
    }

    // Adds [Papyrus] bEnableLogging/bEnableTrace/bLoadDebugInformation to Fallout4Custom.ini.
    public static void EnablePapyrusLogging()
    {
        var path = Paths.CustomIni;
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var wanted = new[] { "bEnableLogging=1", "bEnableTrace=1", "bLoadDebugInformation=1" };
        int sec = lines.FindIndex(l => l.Trim().Equals("[Papyrus]", StringComparison.OrdinalIgnoreCase));
        if (sec < 0)
        {
            lines.Add("[Papyrus]");
            lines.AddRange(wanted);
        }
        else
        {
            int end = lines.FindIndex(sec + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            foreach (var kv in wanted)
            {
                var key = kv.Split('=')[0];
                int idx = lines.FindIndex(sec + 1, end - sec - 1,
                    l => l.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) lines[idx] = kv;
                else { lines.Insert(end, kv); end++; }
            }
        }
        File.WriteAllLines(path, lines);
    }
}
