using System.Diagnostics;

// Fixture for vs_read_output. Everything interesting here goes to the DEBUG pane, not to stdout - which
// is exactly why the tool exists. Run this under F5, then ask for pane 'debug'.

Console.WriteLine("BuildBreak.App starting"); // stdout: goes to the console window, NOT the Debug pane

Debug.WriteLine("[BuildBreak] trace: warming up");
Trace.WriteLine("[BuildBreak] trace: Trace.WriteLine lands in the same pane");

for (int i = 1; i <= 3; i++)
    Debug.WriteLine($"[BuildBreak] order {i} total = {100m + i:0.00}");

// The headline case: an exception that is caught and swallowed. Nothing reaches stdout, the program exits
// zero, and a terminal sees nothing at all - but the Debug pane records the first-chance throw:
//   Exception thrown: 'System.DivideByZeroException' in BuildBreak.App.dll
try
{
    int zero = int.Parse("0");
    _ = 10 / zero;
}
catch (DivideByZeroException)
{
    Debug.WriteLine("[BuildBreak] swallowed a DivideByZeroException - only the Debug pane knows");
}

Console.WriteLine("BuildBreak.App done");
