

using System;
using System.Diagnostics;

namespace PerfUnit.SharedStandard;


/// <summary>
/// Marks a test method as a performance test to be benchmarked and validated by PerfUnit.
/// Use in combination with <see cref="PerfMemoryAttribute"/> and/or <see cref="PerfSpeedAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerformanceFactAttribute : Attribute
{

}

public enum MustTake
{
    LessThan,
    LessThanOrEqualTo,
}

public enum MustUse
{
    LessThan,
    LessThanOrEqualTo,
}

public enum TimeUnit
{
    Nanoseconds,
    Microseconds,
    Milliseconds,
    Seconds,
    Minutes
}


public enum SizeUnit
{
    Bytes,
    Kilobytes,
    Megabytes
}


/// <summary>
/// Specifies a maximum allowed execution time for a performance test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerfSpeedAttribute : Attribute
{
    public MustTake Comparison { get; }
    public double Value { get; }
    public TimeUnit Unit { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PerfSpeedAttribute"/> class.
    /// </summary>
    /// <param name="comparison">The comparison type (e.g., MustTake.LessThan).</param>
    /// <param name="value">The time value to compare against.</param>
    /// <param name="unit">The unit of time (default: Milliseconds).</param>
    public PerfSpeedAttribute(MustTake comparison, double value, TimeUnit unit = TimeUnit.Milliseconds)
    {
        Comparison = comparison;
        Value = value;
        Unit = unit;

    }

}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerfMemoryAttribute : Attribute
{
    public MustUse Comparison { get; }
    public double Value { get; }
    public SizeUnit Unit { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PerfMemoryAttribute"/> class.
    /// </summary>
    /// <param name="comparison">The comparison type (e.g., MustUse.LessThan).</param>
    /// <param name="value">The memory value to compare against.</param>
    /// <param name="unit">The unit of memory size (default: Bytes).</param>
    public PerfMemoryAttribute(MustUse comparison, double value, SizeUnit unit = SizeUnit.Bytes)
    {
        Comparison = comparison;
        Value = value;
        Unit = unit;

    }

}


