using System;

namespace PerfUnit.SharedStandard;

public static class PerformanceMarkerExtensions
{
    /// <summary>
    /// Marks a value-returning expression for performance measurement.
    /// This method is a no-op and simply returns the value.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value to mark for benchmarking.</param>
    /// <returns>The same value passed in.</returns>
    public static T Perf<T>(this T value) => value;

    /// <summary>
    /// Marks an <see cref="Action"/> for performance measurement.
    /// This method is a no-op and simply invokes the action.
    /// </summary>
    /// <param name="action">The action to mark for benchmarking.</param>
    public static void Perf(this Action action) => action();

    /// <summary>
    /// Marks an <see cref="Action"/> as the target for performance measurement.
    /// This method is a no-op and simply invokes the action.
    /// </summary>
    /// <param name="action">The action to benchmark.</param>
    public static void PerfTarget(Action action) => action();

    /// <summary>
    /// Marks a <see cref="Func{T}"/> as the target for performance measurement.
    /// This method is a no-op and simply invokes the function and returns its result.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="func">The function to benchmark.</param>
    /// <returns>The result of invoking <paramref name="func"/>.</returns>
    public static T PerfTarget<T>(Func<T> func) => func();
}
