

using System;
using System.Diagnostics;

namespace PerfUnit.SharedStandard;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerformanceFactAttribute : Attribute
{

}

public enum MustTake
{
    LessThan,
    LessThanOrEqualTo,
    // ...other comparisons
}

public enum MustUse
{
    LessThan,
    LessThanOrEqualTo,
    // ...other comparisons
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


[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerfSpeedAttribute : Attribute
{
    public MustTake Comparison { get; }
    public double Value { get; }
    public TimeUnit Unit { get; }

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

    public PerfMemoryAttribute(MustUse comparison, double value, SizeUnit unit = SizeUnit.Bytes)
    {
        Comparison = comparison;
        Value = value;
        Unit = unit;

    }

}
