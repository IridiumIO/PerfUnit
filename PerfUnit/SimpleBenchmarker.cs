using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


public static class SimpleBenchmarker
{

    private static readonly int discardCount = 1;
    private static readonly double desiredZ = 1.96;
    private static readonly double desiredRelativeMargin = 0.005; 

    private static readonly Stopwatch stopwatch = new Stopwatch();


    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TrulyEmpty() { GC.KeepAlive(EmptyAction); }
    static readonly Action EmptyAction = TrulyEmpty;

    public static Action<string> Output { get; set; } = Console.WriteLine;

    private record PhaseResult(double AvgNsPerOp, int[] OpsPerRound, double[] NsPerOpRounds, double AverageBytes);

    private static PhaseResult RunPhase(string phaseName, Action action, int invocations, int minIterations, int maxIterations)
    {


        maxIterations += discardCount;
        var nsPerOp = new List<double>(maxIterations);
        var opsPerIteration = new List<int>(maxIterations);
        var allocatedBytesPerOp = new List<long>(maxIterations);

        var requiredIterations = maxIterations;
        var runIterations = 0;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            runIterations++;
            Task.Delay(10).Wait();

            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

            stopwatch.Restart();
            for (int i = 0; i < invocations; i++) action();
            stopwatch.Stop();

            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

            allocatedBytesPerOp.Add((afterAlloc - beforeAlloc) / invocations);

            double ns = (stopwatch.ElapsedTicks * 1_000_000_000.0) / (Stopwatch.Frequency * invocations);
            nsPerOp.Add(ns);
            opsPerIteration.Add(invocations);

            GC.Collect();

            if (nsPerOp.Count > discardCount + 4)
            {
                var filtered = nsPerOp.Skip(discardCount).FilterIQR();
                var (mean, stddev, margin) = GetStatistical(filtered, desiredZ);

                requiredIterations = (int)Math.Ceiling(Math.Pow(desiredZ * stddev / (desiredRelativeMargin * mean), 2));
                if (filtered.Length >= Math.Max(requiredIterations, minIterations) && margin / mean < desiredRelativeMargin)
                    break;
            }
        }

        double[] used = nsPerOp.Skip(discardCount).FilterIQR();
        double avg = used.Median();
        double[] usedAlloc = allocatedBytesPerOp.Skip(discardCount).Select(x => (double)x).FilterIQR();
        long avgAllocatedBytes = usedAlloc.Length > 0 ? (long)usedAlloc.Average() : 0;

        // Output phase summary
        var output = $"| Rounds: {runIterations,3} | Ops/Round: {invocations,9} | Avg: {FormatTime(avg),10}/op | Memory: {FormatMemory(avgAllocatedBytes),9}";
        if (used.Length > 1)
        {
            var (_, _, margin) = GetStatistical(used, desiredZ);
            output += $" | CI: +/-{FormatTime(margin),8}";
        }
        Output(output);

        return new PhaseResult(avg, opsPerIteration.Skip(discardCount).ToArray(), used, avgAllocatedBytes);
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
        int minTotalMilliseconds = 100,

        int minInvocations = 4,

        int minWarmupCount = 10,
        int maxWarmupCount = 50,
        int warmupCount = 0,

        int minIterationCount = 5,
        int maxIterationCount = 50,
        int iterationCount = 0,

        int jitWarmupInvocations = 10,
        int jitWarmupCount = 3,

        double expectedMaxTimeMs = 0,
        double expectedMaxMemoryBytes = 0
        )
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

        if (minTotalMilliseconds < 50)
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


        Output("Jit Overhead");
        RunPhase("JIT Overhead", EmptyAction, jitWarmupInvocations, jitWarmupCount, jitWarmupCount);


        Output("Jit Runner");
        var jitResult = RunPhase("JIT Runner", action, jitWarmupInvocations, jitWarmupCount, jitWarmupCount);
        if (TryShortCircuit(jitResult, expectedMaxTimeMs, expectedMaxMemoryBytes)) return (jitResult.AvgNsPerOp, jitResult.AverageBytes);


        int invocations = Math.Max(minInvocations, EstimateInvocations(action, minTotalMilliseconds));


        Output("Warmup Overhead");
        RunPhase("Warmup Overhead", EmptyAction, invocations, minWarmupCount, maxWarmupCount);


        Output("Actual Overhead");
        var (avgOverheadNsPerOp, _, _, _) = RunPhase("Actual Overhead", EmptyAction, invocations, minIterationCount, maxIterationCount);


        Output("Warmup Runner");
        var warmupResult = RunPhase("Warmup Runner", action, invocations, minWarmupCount, maxWarmupCount);
        if (TryShortCircuit(warmupResult, expectedMaxTimeMs, expectedMaxMemoryBytes)) return (warmupResult.AvgNsPerOp, warmupResult.AverageBytes);


        Output("Actual Runner");
        var (avgActualNsPerOp, opsPerRound, nsPerOpRounds, bytesUsed) = RunPhase("Actual Runner", action, invocations, minIterationCount, maxIterationCount);


        double avgNetNsPerOp = Math.Max(0, nsPerOpRounds.Select(ns => ns - avgOverheadNsPerOp).Median());

        Output("");
        Output($"Final Result     Time:{FormatTime(avgNetNsPerOp)}   Ops:{opsPerRound.Sum(x => (long)x),9}    Memory:{bytesUsed,10}b");
        Output(new string('=', 96));
        Output("");

        return (avgNetNsPerOp, bytesUsed);
    }


    private static bool TryShortCircuit(
        PhaseResult result,
        double expectedMaxTimeMs,
        double expectedMaxMemoryBytes)
    {

        var expectedMaxTimeNs = expectedMaxTimeMs * 1_000_000; // Convert ms to ns

        // Both thresholds specified: require both to be satisfied
        if (expectedMaxTimeNs > 0 && expectedMaxMemoryBytes > 0)
        {
            if (result.AvgNsPerOp < expectedMaxTimeNs && result.AverageBytes < expectedMaxMemoryBytes)
            {
                Output($"| Avg Time: {FormatTime(result.AvgNsPerOp)}   Memory: {FormatMemory(result.AverageBytes)}");
                Output("Skipping further measurements as both time and memory usage are acceptable.");
                return true;
            }
        }
        // Only time threshold specified
        else if (expectedMaxTimeNs > 0 && expectedMaxMemoryBytes == 0)
        {
            if (result.AvgNsPerOp < expectedMaxTimeNs)
            {
                Output($"| Avg Time: {FormatTime(result.AvgNsPerOp)}   Memory: {FormatMemory(result.AverageBytes)}");
                Output("Skipping further measurements as time is acceptable.");
                return true;
            }
        }
        // Only memory threshold specified
        else if (expectedMaxMemoryBytes > 0 && expectedMaxTimeNs == 0)
        {
            if (result.AverageBytes < expectedMaxMemoryBytes)
            {
                Output($"| Avg Time: {FormatTime(result.AvgNsPerOp)}   Memory: {FormatMemory(result.AverageBytes)}");
                Output("Skipping further measurements as memory usage is acceptable.");
                return true;
            }
        }
        return false;
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

    private static string FormatMemory(double bytes)
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
