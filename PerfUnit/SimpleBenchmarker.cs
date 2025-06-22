using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


public static class SimpleBenchmarker
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TrulyEmpty() { GC.KeepAlive(EmptyAction); }
    static readonly Action EmptyAction = TrulyEmpty;

    static readonly int discardCount = 1;
    static readonly double desiredZ = 1.96; // 95% confidence
    static readonly double desiredRelativeMargin = 0.005; // 0.5% margin

    static readonly Stopwatch stopwatch = new Stopwatch();

    private static (double avgNsPerOp, int[] opsPerRound, double[] nsPerOpRounds, double averageBytes) RunPhase(string phaseName,
        Action action, int invocations, int minIterations = 15, int maxIterations = 50, bool leanMode = false)
    {

        if (leanMode)
        {
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                for (int i = 0; i < invocations; i++) action();
            }
            return (0, Array.Empty<int>(), Array.Empty<double>(), 0);
        }

        maxIterations += discardCount;
        var nsPerOp = new List<double>(maxIterations);
        var opsPerIteration = new List<int>(maxIterations);

        var allocatedBytesPerOp = new List<long>(maxIterations);

        var requiredIterations = int.MaxValue;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            Task.Delay(10).Wait();

            // Record GC collection counts before
            int[] gcCollectionsBefore = new int[GC.MaxGeneration + 1];
            for (int gen = 0; gen <= GC.MaxGeneration; gen++)
                gcCollectionsBefore[gen] = GC.CollectionCount(gen);

            // Record memory before
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Restart();

            for (int i = 0; i < invocations; i++) action();

            stopwatch.Stop();

            // Record GC collection counts after
            int[] gcCollectionsAfter = new int[GC.MaxGeneration + 1];
            for (int gen = 0; gen <= GC.MaxGeneration; gen++)
                gcCollectionsAfter[gen] = GC.CollectionCount(gen);

            // Record memory after
            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

            long allocDiff = afterAlloc - beforeAlloc;
            allocatedBytesPerOp.Add(allocDiff / invocations);

            double ns = (stopwatch.ElapsedTicks * 1_000_000_000.0) / (Stopwatch.Frequency * invocations);
            nsPerOp.Add(ns);
            opsPerIteration.Add(invocations);

            GC.Collect();


            if (nsPerOp.Count > discardCount + 4)
            {
                // Use only the non-discarded results for statistics
                var valid = nsPerOp.Skip(discardCount).ToArray();
                var filtered = valid.FilterIQR();
                var (mean, stddev, margin) = GetStatistical(filtered, desiredZ);

                // Calculate required rounds for current stddev/mean
                requiredIterations = (int)Math.Ceiling(Math.Pow(desiredZ * stddev / (desiredRelativeMargin * mean), 2));

                // Stop if we have enough rounds and the margin is satisfied
                if (filtered.Length >= Math.Max(requiredIterations, minIterations) && margin / mean < desiredRelativeMargin)
                {
                    break;
                }
            }
 
        }

        // Use only the non-discarded results for final statistics
        double[] used = nsPerOp.Skip(discardCount).FilterIQR();
        double avg = used.Median();

        // Memory: discard warmup, filter outliers, then average
        double[] usedAlloc = allocatedBytesPerOp.Skip(discardCount).Select(x => (double)x).FilterIQR();
        long avgAllocatedBytes = usedAlloc.Length > 0 ? (long)usedAlloc.Average() : 0;

        Console.Write($"| Avg: {FormatTime(avg),2}/op    Memory: {FormatMemory(avgAllocatedBytes),1}");

        if (used.Length > 1)
        {
            var (_, _, margin) = GetStatistical(used, desiredZ);
            Console.Write($"  CI: +/-{FormatTime(margin),2:F4}");
        }
        Console.WriteLine();

        // Also skip the opsPerRound for the discarded rounds
        return (avg, opsPerIteration.Skip(discardCount).ToArray(), used, avgAllocatedBytes);
    }


    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (double mean, double stddev, double margin) GetStatistical(double[] data, double desiredZ)
    {
        double mean = data.Average();
        double stddev = Math.Sqrt(data.Sum(x => (x - mean) * (x - mean)) / (data.Length - 1));
        double margin = desiredZ * stddev / Math.Sqrt(data.Length);

        return (mean, stddev, margin);
    }

    public static int EstimateInvocations(Action action, int minTotalMilliseconds)
    {
        int iterations = 1;
        while (true)
        {
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++) action();

            sw.Stop();

            if (sw.ElapsedMilliseconds >= minTotalMilliseconds) break;

            iterations = Math.Min(iterations * 2, 536_870_912);
            if (iterations == 536_870_912) break;

        }
        return iterations;
    }


    public static (double, double) Run(
        Action action,
        int minTotalMilliseconds = 500,
        int minInvocations = 4,

        int minWarmupCount = 10,
        int maxWarmupCount = 50,
        int warmupCount = 0,

        int minIterationCount = 5,
        int maxIterationCount = 50,
        int iterationCount = 0,

        int jitWarmupInvocations = 10,
        int jitWarmupCount = 3)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;


        if (warmupCount > 0)
        {
            minWarmupCount = warmupCount;
            maxWarmupCount = warmupCount;
        }
        if (iterationCount > 0)
        {
            minIterationCount = iterationCount;
            maxIterationCount = iterationCount;
        }

        if (minTotalMilliseconds < 100)
            throw new ArgumentException("minTotalMilliseconds must be at least 100 milliseconds");
        if (minInvocations < 1)
            throw new ArgumentException("minInvocations must be at least 1");
        if (minWarmupCount < 1)
            throw new ArgumentException("minWarmupCount must be at least 1");
        if (minWarmupCount > maxWarmupCount)
            throw new ArgumentException("maxWarmupCount must be greater than or equal to minWarmupCount");
        if (minIterationCount < 1)
            throw new ArgumentException("minIterationCount must be at least 1");
        if (minIterationCount > maxIterationCount)
            throw new ArgumentException("maxIterationCount must be greater than or equal to minIterationCount");
        if (jitWarmupInvocations < 1)
            throw new ArgumentException("jitWarmupInvocations must be at least 1");
        if (jitWarmupCount < 1)
            throw new ArgumentException("jitWarmupCount must be at least 1");
        if (action == null)
            throw new ArgumentNullException(nameof(action), "Action cannot be null");


        Console.WriteLine("Jit Overhead");
        RunPhase("JIT Overhead", EmptyAction, jitWarmupInvocations, jitWarmupCount, jitWarmupCount);


        Console.WriteLine("Jit Runner");
        RunPhase("JIT Runner", action, jitWarmupInvocations, jitWarmupCount, jitWarmupCount);

        int invocations = Math.Max(minInvocations, EstimateInvocations(action, minTotalMilliseconds));

        Console.WriteLine("Warmup Overhead");
        RunPhase("Warmup Overhead", EmptyAction, invocations, minWarmupCount, maxWarmupCount);

        Console.WriteLine("Actual Overhead");
        var (avgOverheadNsPerOp, _, _, _) = RunPhase("Actual Overhead", EmptyAction, invocations, minIterationCount, maxIterationCount);

        Console.WriteLine("Warmup Runner");
        RunPhase("Warmup Runner", action, invocations, minWarmupCount, maxWarmupCount);

        Console.WriteLine("Actual Runner");
        var (avgActualNsPerOp, opsPerRound, nsPerOpRounds, bytesUsed) = RunPhase("Actual Runner", action, invocations, minIterationCount, maxIterationCount);

        double avgNetNsPerOp = Math.Max(0, nsPerOpRounds.Select(ns => ns - avgOverheadNsPerOp).Median());

        Console.WriteLine($"Final Result     Time:{FormatTime(avgNetNsPerOp)}   Ops:{opsPerRound.Sum(x => (long)x),9}    Memory:{bytesUsed,10}b");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine();

        return (avgNetNsPerOp, bytesUsed);
    }


    private static string FormatTime(double nanosecondtime)
    {
        return nanosecondtime switch
        {
            < 1_0 => $"{nanosecondtime,6:F4} ns",
            < 1_000 => $"{nanosecondtime,6:F2} ns",
            < 1_000_000 => $"{(nanosecondtime / 1_000),6:F2} us",
            < 1_000_000_000 => $"{(nanosecondtime / 1_000_000),6:F2} ms",
            _ => $"{(nanosecondtime / 1_000_000_000),6:F2} s"
        };
    }

    private static string FormatMemory(long bytes)
    {
        return bytes switch
        {
            < 1_000 => $"{bytes,6} B",
            < 1_000_000 => $"{(bytes / 1_000.0),6:F2} KB",
            < 1_000_000_000 => $"{(bytes / 1_000_000.0),6:F2} MB",
            _ => $"{(bytes / 1_000_000_000.0),6:F2} GB"
        };
    }


    public static (double, double) RunVolatileFast(Action action)
    {
        return Run(action, 100, 1, 1, 1, 1, 1, 1, 1, 1, 1);
    }








}


public static class EnumerableExtensions
{
    public static double Median(this IEnumerable<double> source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var sorted = source.OrderBy(x => x).ToArray();
        int count = sorted.Length;
        if (count == 0) throw new InvalidOperationException("Sequence contains no elements");

        int mid = count / 2;
        if (count % 2 == 0)
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        else
            return sorted[mid];
    }

    public static double[] FilterIQR(this IEnumerable<double> source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var sorted = source.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        if (n < 4) return sorted; // Not enough data for IQR, return as is

        double q1 = sorted[n / 4];
        double q3 = sorted[(3 * n) / 4];
        double iqr = q3 - q1;
        double lower = q1 - 1.5 * iqr;
        double upper = q3 + 1.5 * iqr;
        return sorted.Where(x => x >= lower && x <= upper).ToArray();
    }

}
