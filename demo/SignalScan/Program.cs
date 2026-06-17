using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalScan;

// Computes sliding-window statistics over a stream of integer readings: every contiguous window of a
// given size, the sum of each window, and the highest-sum ("peak") window. Compiles and runs, and the
// output is self-checking. The logic flaw here is left deliberately UNcommented - it has to be found
// from runtime behaviour, not from a hint in the source.

public static class WindowAnalyzer
{
    /// <summary>Return every contiguous window of <paramref name="size"/> readings and each window's sum.</summary>
    public static (List<int[]> windows, List<int> sums) Scan(int[] readings, int size)
    {
        var windows = new List<int[]>();
        var sums = new List<int>();
        var buffer = new int[size];

        for (int start = 0; start + size <= readings.Length; start++)
        {
            int sum = 0;
            for (int j = 0; j < size; j++)
            {
                buffer[j] = readings[start + j];
                sum += buffer[j];
            }
            windows.Add(buffer);
            sums.Add(sum);
        }

        return (windows, sums);
    }

    /// <summary>Index of the window with the greatest sum (first one wins on ties).</summary>
    public static int IndexOfPeak(List<int> sums)
    {
        int best = 0;
        for (int i = 1; i < sums.Count; i++)
            if (sums[i] > sums[best]) best = i;
        return best;
    }
}

public static class Program
{
    public static void Main()
    {
        int[] readings = { 3, 1, 4, 9, 5, 2, 6, 1 };
        int size = 3;

        var (windows, sums) = WindowAnalyzer.Scan(readings, size);
        int peak = WindowAnalyzer.IndexOfPeak(sums);

        int[] peakWindow = windows[peak];
        int reportedSum = sums[peak];

        Console.WriteLine($"Scanned {windows.Count} windows of size {size} over {readings.Length} readings.");
        Console.WriteLine($"Peak window at index {peak}: [{string.Join(", ", peakWindow)}], reported sum = {reportedSum}.");

        int recomputed = peakWindow.Sum();
        if (recomputed == reportedSum)
            Console.WriteLine("OK");
        else
            Console.WriteLine($"INCONSISTENT: the reported peak window [{string.Join(", ", peakWindow)}] actually sums to {recomputed}, not {reportedSum}.");
    }
}
