using System;
using System.Collections.Generic;
using System.Text;

namespace PerfUnit.SharedStandard;

public static class Format
{

    public static string FormatTime(double nanosecondtime, bool ignorePadding = false)
    {
        return nanosecondtime switch
        {
            < 10 => ignorePadding
                ? $"{nanosecondtime:F4} ns"
                : $"{nanosecondtime,6:F4} ns",
            < 1_000 => ignorePadding
                ? $"{nanosecondtime:F2} ns"
                : $"{nanosecondtime,6:F2} ns",
            < 1_000_000 => ignorePadding
                ? $"{(nanosecondtime / 1_000):F2} us"
                : $"{(nanosecondtime / 1_000),6:F2} us",
            < 1_000_000_000 => ignorePadding
                ? $"{(nanosecondtime / 1_000_000):F2} ms"
                : $"{(nanosecondtime / 1_000_000),6:F2} ms",
            _ => ignorePadding
                ? $"{(nanosecondtime / 1_000_000_000):F2} s"
                : $"{(nanosecondtime / 1_000_000_000),6:F2} s"
        };
    }

    public static string FormatMemory(double bytes, bool ignorePadding = false)
    {
        return bytes switch
        {
            < 1_000 => ignorePadding
                ? $"{bytes:F2} B"
                : $"{bytes,6:F2} B",
            < 1_000_000 => ignorePadding
                ? $"{(bytes / 1_000):F2} KB"
                : $"{(bytes / 1_000),6:F2} KB",
            < 1_000_000_000 => ignorePadding
                ? $"{(bytes / 1_000_000):F2} MB"
                : $"{(bytes / 1_000_000),6:F2} MB",
            _ => ignorePadding
                ? $"{(bytes / 1_000_000_000):F2} GB"
                : $"{(bytes / 1_000_000_000),6:F2} GB"
        };
    }

}
