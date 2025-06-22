using System;

namespace PerfUnit.SharedStandard;

public static class PerformanceMarkerExtensions
{

    public static T Perf<T>(this T value) => value;


}
