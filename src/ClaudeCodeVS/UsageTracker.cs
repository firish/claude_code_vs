using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PLog = ClaudeCodeVs.Protocol.Log;

namespace ClaudeCodeVs;

/// <summary>
/// Parses the Claude Code conversation transcript (a JSONL the CLI writes) to aggregate token usage and
/// an estimated cost for the session, which the dockable panel shows. The Stop hook hands us the
/// transcript path via POST /usage. The IDE protocol exposes none of this - the transcript is the only
/// source. Both the format and the prices are undocumented/version-fragile, so parse defensively and
/// label the cost an estimate.
/// </summary>
internal static class UsageTracker
{
    /// <summary>USD per 1,000,000 tokens. Approx public list prices as of 2026-01; cost is an estimate.</summary>
    private readonly struct Price
    {
        public Price(double input, double output, double cacheWrite, double cacheRead)
        { Input = input; Output = output; CacheWrite = cacheWrite; CacheRead = cacheRead; }
        public double Input { get; }
        public double Output { get; }
        public double CacheWrite { get; }
        public double CacheRead { get; }
    }

    private static Price PriceFor(string? model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        if (m.Contains("opus")) return new Price(15.0, 75.0, 18.75, 1.50);
        if (m.Contains("haiku")) return new Price(1.0, 5.0, 1.25, 0.10);
        return new Price(3.0, 15.0, 3.75, 0.30); // default: Sonnet tier
    }

    public static async Task UpdateFromTranscriptAsync(string transcriptPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(transcriptPath)) return;

            // Copy the text out under a shared read lock (the CLI is still writing it).
            string text;
            using (var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = await sr.ReadToEndAsync();

            long input = 0, output = 0, cacheRead = 0;
            int turns = 0;
            double cost = 0;
            string? model = null;
            long lastIn = 0, lastOut = 0, lastCacheRead = 0;
            double lastCost = 0;

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject o;
                try { o = JObject.Parse(line); } catch { continue; }
                if ((string?)o["type"] != "assistant") continue;

                var msg = o["message"] as JObject;
                if (msg?["usage"] is not JObject usage) continue;

                var lineModel = (string?)msg["model"];
                if (!string.IsNullOrEmpty(lineModel)) model = lineModel;

                long i = (long?)usage["input_tokens"] ?? 0;
                long ou = (long?)usage["output_tokens"] ?? 0;
                long cr = (long?)usage["cache_read_input_tokens"] ?? 0;
                long cc = (long?)usage["cache_creation_input_tokens"] ?? 0;

                var p = PriceFor(lineModel ?? model);
                var entryCost = (i * p.Input + ou * p.Output + cc * p.CacheWrite + cr * p.CacheRead) / 1_000_000.0;

                // "Input" = freshly processed input this call: the uncached delta (input_tokens, ~1 when
                // caching) PLUS newly cached tokens (cache_creation). cache_read is the reused bulk,
                // shown separately as "cached". (Showing input_tokens alone reads as a misleading "1".)
                long freshInput = i + cc;

                // Cumulative session totals…
                input += freshInput; output += ou; cacheRead += cr; cost += entryCost; turns++;
                // …and the most recent call (overwritten each iteration -> ends on the last).
                lastIn = freshInput; lastOut = ou; lastCacheRead = cr; lastCost = entryCost;
            }

            Ui.BridgeStatus.SetUsage(
                session: new Ui.BridgeStatus.Usage(input, output, cacheRead, cost),
                latest: new Ui.BridgeStatus.Usage(lastIn, lastOut, lastCacheRead, lastCost),
                turns, model);
            PLog.Info($"usage: latest {lastIn}/{lastOut} · session {input} in / {output} out / {cacheRead} cached, {turns} turns, ~${cost:0.00} est");
        }
        catch (Exception e)
        {
            PLog.Warn($"usage parse failed: {e.Message}");
        }
    }
}
