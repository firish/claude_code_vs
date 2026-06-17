using System;

namespace ComboScore;

// Scores a sequence of rounds. Each positive round adds its points times the current combo multiplier,
// which grows by one for each consecutive scoring round (so a streak of hits is worth progressively
// more). Compiles and runs, and the output is self-checking. The logic flaw is left deliberately
// UNcommented - it has to be found from runtime behaviour, not from a hint in the source.

public static class ComboScorer
{
    public static long Score(int[] rounds)
    {
        long total = 0;
        int combo = 0;

        for (int i = 0; i < rounds.Length; i++)
        {
            int points = rounds[i];
            if (points > 0)
            {
                combo++;
                total += points * combo;
            }
            else if (points < 0)
            {
                combo = 0;
                total += points;
            }
        }

        return total;
    }
}

public static class Program
{
    public static void Main()
    {
        int[] rounds = { 5, 3, 0, 4, 2, 0, 6 };
        const long expected = 25;

        long actual = ComboScorer.Score(rounds);

        Console.WriteLine($"Rounds: [{string.Join(", ", rounds)}]");
        Console.WriteLine($"Computed score: {actual}");
        Console.WriteLine($"Expected score: {expected}");
        Console.WriteLine(actual == expected ? "OK" : $"MISMATCH (off by {actual - expected})");
    }
}
