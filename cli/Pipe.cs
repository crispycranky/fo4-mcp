using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Fo4Cli;

// Client for the F4Exec plugin's named pipe (message mode, one JSON response per request).
static class Pipe
{
    public const string Name = "fo4_console_exec_v1";

    public static JsonObject Request(string request, int connectTimeoutMs = 2000)
    {
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
        try
        {
            pipe.Connect(connectTimeoutMs);
        }
        catch (TimeoutException)
        {
            throw new F4ExecUnavailable(Status.Diagnose());
        }
        pipe.ReadMode = PipeTransmissionMode.Message;

        var bytes = Encoding.UTF8.GetBytes(request);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();

        using var ms = new MemoryStream();
        var buf = new byte[65536];
        do
        {
            int n = pipe.Read(buf, 0, buf.Length);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        } while (!pipe.IsMessageComplete);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("empty response from F4Exec");
    }

    public static JsonObject Ping(int connectTimeoutMs = 500) => Request("ping", connectTimeoutMs);

    public static JsonObject Exec(IEnumerable<string> lines) =>
        Request("exec\n" + string.Join("\n", lines.Select(l => l.Replace("\r", "").Replace("\n", " "))));
}

class F4ExecUnavailable(string reason) : Exception(reason);
