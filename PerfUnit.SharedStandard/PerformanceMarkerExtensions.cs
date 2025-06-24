using System;

namespace PerfUnit.SharedStandard;

public static class PerformanceMarkerExtensions
{

    public static T Perf<T>(this T value) => value;

    public static void Perf(this Action action) => action();

    public static void PerfTarget(Action action) => action();

    // For value-returning methods
    public static T PerfTarget<T>(Func<T> func) => func();
}
