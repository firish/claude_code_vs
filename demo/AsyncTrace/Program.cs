using System;
using System.Threading.Tasks;

namespace AsyncTrace;

// A small async pipeline. RunAsync folds a list by awaiting ComputeAsync on each item; ComputeAsync awaits
// InnerAsync. The awaits resume on threadpool threads (no sync context in a console app, plus
// ConfigureAwait(false) and Task.Yield), so when you pause inside InnerAsync you are on a CONTINUATION -
// the physical thread stack no longer shows ComputeAsync / RunAsync as callers (the runtime suspended them
// as state machines and resumed you elsewhere).
//
// This fixture is for confirming what the debugger surface sees across that boundary:
//   - can it read the CURRENT async frame's args + locals, and evaluate there, with correct post-await
//     values? (expected: yes - the debugger maps the state machine's hoisted fields back to source names)
//   - how much of the LOGICAL async call chain shows up in the call stack / via vs_get_frame_locals on
//     caller frames? (open question: EnvDTE may show the stitched async callers, or only the physical
//     resumed stack - MoveNext / builder / threadpool / [External Code])
//
// Set the breakpoint on the marked line in InnerAsync and run (F5).
internal static class Program
{
    private static async Task Main()
    {
        var items = new[] { 5, 3, 8, 2 };
        int total = await RunAsync(items);
        Console.WriteLine($"total = {total}");
    }

    private static async Task<int> RunAsync(int[] items)
    {
        int running = 0;
        foreach (var n in items)
            running += await ComputeAsync(n, running);
        return running;
    }

    private static async Task<int> ComputeAsync(int n, int soFar)
    {
        await Task.Delay(15).ConfigureAwait(false);   // drop any sync context; resume on the pool
        int contribution = await InnerAsync(n, soFar);
        return contribution;
    }

    private static async Task<int> InnerAsync(int n, int soFar)
    {
        await Task.Yield();                           // guarantee a real async continuation
        int scaled = n * 2;
        int blended = scaled + soFar / 2;
        return blended;                               // <-- set the breakpoint HERE (post-await; n, soFar, scaled, blended all live)
    }
}
