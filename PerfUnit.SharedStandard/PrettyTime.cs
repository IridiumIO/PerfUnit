using System;
using System.Collections.Generic;
using System.Text;

namespace PerfUnit.SharedStandard;

public static class PrettyTime
{

    public static string FormatTime(double nanosecondtime)
    {
        return nanosecondtime switch
        {
            < 1_0 => $"{nanosecondtime,6:F4} ns",
            < 1_000 => $"{nanosecondtime,6:F2} ns",
            < 1_000_000 => $"{(nanosecondtime / 1_000),6:F2} µs",
            < 1_000_000_000 => $"{(nanosecondtime / 1_000_000),6:F2} ms",
            _ => $"{(nanosecondtime / 1_000_000_000),6:F2} s"
        };
    }

}
