internal static class Program
{
    private static void Main()
    {
        // Deliberate compile error: CS0029 cannot implicitly convert 'string' to 'int'.
        // Used to verify getDiagnostics surfaces real C# errors (open this as a project so Roslyn analyzes it).
        int x = "oops";
        System.Console.WriteLine(x);
    }
}
