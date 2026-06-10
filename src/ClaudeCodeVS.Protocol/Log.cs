namespace ClaudeCodeVs.Protocol;

public enum LogLevel { Info, Warn, Error, Event, Frame }

/// <summary>
/// Minimal pluggable logger. The spike wrote to the console; inside the VSIX we redirect <see cref="Sink"/>
/// to a Visual Studio output pane. Kept dependency-free so the protocol core stays pure BCL.
/// Never log the auth token (CLAUDE.md convention #2) — call sites redact it before logging.
/// </summary>
public static class Log
{
    /// <summary>Where log lines go. Defaults to the console; the VSIX replaces this with an output-pane writer.</summary>
    public static Action<LogLevel, string> Sink { get; set; } = (level, msg) =>
        Console.WriteLine($"[{level.ToString().ToLowerInvariant()}] {msg}");

    public static void Info(string msg) => Sink(LogLevel.Info, msg);
    public static void Warn(string msg) => Sink(LogLevel.Warn, msg);
    public static void Error(string msg) => Sink(LogLevel.Error, msg);
    public static void Event(string msg) => Sink(LogLevel.Event, msg);

    /// <summary>Log a JSON-RPC frame. inbound = received from the CLI; outbound = sent to the CLI.</summary>
    public static void Frame(bool inbound, string json)
        => Sink(LogLevel.Frame, $"{(inbound ? "<<" : ">>")} {json}");
}
