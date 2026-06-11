internal static class Program
{
    private static void Main()
    {
        int x = 0;
        x += 1;
        System.Console.WriteLine(x);
        System.Console.WriteLine("done");
        System.Console.WriteLine($"x squared = {x * x}");

        for (int i = 0; i < 8; i++)
        {
            double delay = Math.Pow(2, i);
            System.Console.WriteLine($"Attempt {i + 1}: retry in {delay}s");
        }
    }
}
