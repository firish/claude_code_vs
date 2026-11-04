using Newtonsoft.Json;

namespace GraphProbe.Core;

/// <summary>A message wrapper - just enough code for the project to have a reason to reference a package.</summary>
public sealed class Envelope
{
    public Envelope(string kind, string payload)
    {
        Kind = kind;
        Payload = payload;
    }

    public string Kind { get; }
    public string Payload { get; }

    public string ToJson() => JsonConvert.SerializeObject(this);
}
