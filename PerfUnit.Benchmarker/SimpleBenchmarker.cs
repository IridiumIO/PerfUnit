using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static PerfUnit.SharedStandard.Format;

namespace PerfUnit.Benchmarker;

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


    public static (double, double) Run(Action action, BenchmarkConfig? config = null)
    {

        config ??= new BenchmarkConfig();

        ValidateConfig(action, config);

        int minTotalMilliseconds = config.MinTotalMilliseconds;
        int minInvocations = config.MinInvocations;
        int minWarmupCount = config.MinWarmupCount;
        int maxWarmupCount = config.MaxWarmupCount;
        int warmupCount = config.WarmupCount;
        int minIterationCount = config.MinIterationCount;
        int maxIterationCount = config.MaxIterationCount;
        int iterationCount = config.IterationCount;
        int jitWarmupInvocations = config.JitWarmupInvocations;
        int jitWarmupCount = config.JitWarmupCount;
        double expectedMaxTimeMs = config.ExpectedMaxTimeMs;
        double expectedMaxMemoryBytes = config.ExpectedMaxMemoryBytes;


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


    public static (double, double) RunVolatileFast(Action action)
    {
        return Run(action, BenchmarkConfig.VolatileFastConfig());
    }


    private static void ValidateConfig(Action action, BenchmarkConfig config)
    {
        if (config.MinTotalMilliseconds < 50)
            throw new ArgumentException("minTotalMilliseconds must be at least 100 milliseconds");
        if (config.MinInvocations < 1)
            throw new ArgumentException("minInvocations must be at least 1");
        if (config.MinWarmupCount < 1)
            throw new ArgumentException("minWarmupCount must be at least 1");
        if (config.MinWarmupCount > config.MaxWarmupCount)
            throw new ArgumentException("maxWarmupCount must be greater than or equal to minWarmupCount");
        if (config.MinIterationCount < 1)
            throw new ArgumentException("minIterationCount must be at least 1");
        if (config.MinIterationCount > config.MaxIterationCount)
            throw new ArgumentException("maxIterationCount must be greater than or equal to minIterationCount");
        if (config.JitWarmupInvocations < 1)
            throw new ArgumentException("jitWarmupInvocations must be at least 1");
        if (config.JitWarmupCount < 1)
            throw new ArgumentException("jitWarmupCount must be at least 1");
        if (action == null)
            throw new ArgumentNullException(nameof(action), "Action cannot be null");
    }

    private static bool TryShortCircuit(PhaseResult result, double expectedMaxTimeMs, double expectedMaxMemoryBytes)
    {

        var expectedMaxTimeNs = expectedMaxTimeMs * 1_000_000; // Convert ms to ns

        // Both thresholds specified: require both to be satisfied
        if (expectedMaxTimeNs >= 0 && expectedMaxMemoryBytes >= 0)
        {
            if (result.AvgNsPerOp < expectedMaxTimeNs && result.AverageBytes < expectedMaxMemoryBytes)
            {
                Output($"| Avg Time: {FormatTime(result.AvgNsPerOp)}   Memory: {FormatMemory(result.AverageBytes)}");
                Output("Skipping further measurements as both time and memory usage are acceptable.");
                return true;
            }
        }

        // Only time threshold specified
        else if (expectedMaxTimeNs >= 0 && expectedMaxMemoryBytes == -1)
        {
            if (result.AvgNsPerOp < expectedMaxTimeNs)
            {
                Output($"| Avg Time: {FormatTime(result.AvgNsPerOp)}   Memory: {FormatMemory(result.AverageBytes)}");
                Output("Skipping further measurements as time is acceptable.");
                return true;
            }
        }

        // Only memory threshold specified
        else if (expectedMaxMemoryBytes >= 0 && expectedMaxTimeNs < 0)
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



}


public class BenchmarkConfig
{
    public int MinTotalMilliseconds { get; set; } = 100;

    public int MinInvocations { get; set; } = 4;

    public int MinWarmupCount { get; set; } = 10;
    public int MaxWarmupCount { get; set; } = 50;
    public int WarmupCount { get; set; } = 0;

    public int MinIterationCount { get; set; } = 5;
    public int MaxIterationCount { get; set; } = 50;
    public int IterationCount { get; set; } = 0;

    public int JitWarmupInvocations { get; set; } = 10;
    public int JitWarmupCount { get; set; } = 3;

    public double ExpectedMaxTimeMs { get; set; } = -1;
    public double ExpectedMaxMemoryBytes { get; set; } = -1;

    public BenchmarkConfig() { }

    public BenchmarkConfig(int minTotalMilliseconds = 100, int minInvocations = 4, int minWarmupCount=10, int maxWarmupCount=50, int warmupCount=0,
        int minIterationCount=5, int maxIterationCount=50, int iterationCount=0, int jitWarmupInvocations=10, int jitWarmupCount=3,
        double expectedMaxTimeMs=-1, double expectedMaxMemoryBytes=-1)
    {
        MinTotalMilliseconds = minTotalMilliseconds;
        MinInvocations = minInvocations;
        MinWarmupCount = minWarmupCount;
        MaxWarmupCount = maxWarmupCount;
        WarmupCount = warmupCount;
        MinIterationCount = minIterationCount;
        MaxIterationCount = maxIterationCount;
        IterationCount = iterationCount;
        JitWarmupInvocations = jitWarmupInvocations;
        JitWarmupCount = jitWarmupCount;
        ExpectedMaxTimeMs = expectedMaxTimeMs;
        ExpectedMaxMemoryBytes = expectedMaxMemoryBytes;
    }


    public static BenchmarkConfig VolatileFastConfig() => new BenchmarkConfig(100, 1, 1, 1, 1, 1, 1, 1, 1, 1);

}