using System.Text.Json;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// Tiny structured logger. The one feature that matters for a protocol spike is
/// <see cref="Frame"/>: every JSON-RPC message in or out is logged verbatim so we can
/// see exactly what the (undocumented) CLI sends and what we send back.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    // ANSI colours so inbound/outbound frames are easy to tell apart in a terminal.
    private const string Reset = "[0m";
    private const string Dim = "[2m";
    private const string Cyan = "[36m";   // inbound  (CLI -> us)
    private const string Green = "[32m";  // outbound (us -> CLI)
    private const string Yellow = "[33m"; // events
    private const string Red = "[31m";    // errors

    public static void Info(string msg) => Write(Yellow, "INFO ", msg);
    public static void Warn(string msg) => Write(Red, "WARN ", msg);
    public static void Error(string msg) => Write(Red, "ERROR", msg);
    public static void Event(string msg) => Write(Dim, "·    ", msg);

    /// <summary>Log a JSON-RPC frame. <paramref name="inbound"/> = CLI→us, else us→CLI.</summary>
    public static void Frame(bool inbound, string json)
    {
        var arrow = inbound ? "<<" : ">>";
        var colour = inbound ? Cyan : Green;
        Write(colour, arrow + "   ", Pretty(json));
    }

    private static void Write(string colour, string tag, string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (Gate)
        {
            Console.WriteLine($"{Dim}{ts}{Reset} {colour}{tag}{Reset} {msg}");
        }
    }

    // Re-serialise compactly so multi-line frames don't drown the log, but keep it readable.
    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, CompactOptions);
        }
        catch
        {
            return json; // not JSON (shouldn't happen) — log raw
        }
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };
}
