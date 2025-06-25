using System;
using System.Collections.Generic;
using System.Linq;

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
