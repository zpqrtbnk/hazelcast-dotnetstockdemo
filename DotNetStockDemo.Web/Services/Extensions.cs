using System.Numerics;
using Hazelcast.Models;

namespace DotNetStockDemo.Web.Services;

internal static class Extensions
{
    public static string ToString(this HBigDecimal value, int decimalsCount)
    {
        var text = value.ToString();
        var separator = '.'; //CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var pos = text.IndexOf(separator);
        return pos < 0 ? text : text.Substring(0, Math.Min(text.Length, pos + decimalsCount + 1));
    }

    public static double ToDouble(this HBigDecimal value) => TryToDouble(value, out var doubleValue)
        ? doubleValue
        : throw new OverflowException($"Value was either too large or too small for {typeof(double)}");

    public static bool TryToDouble(this HBigDecimal value, out double doubleValue)
    {
        BigInteger Pow10(int power) => BigInteger.Pow(new BigInteger(10), power);

        var n = value.Scale >= 0 ? value : new HBigDecimal(value.UnscaledValue * Pow10(-value.Scale));
        var (u, s) = (n.UnscaledValue, n.Scale);

        var d = Pow10(s);
        var remainder = BigInteger.Remainder(u, d);
        var scaled = BigInteger.Divide(u, d);

        if (scaled > new BigInteger(double.MaxValue))
        {
            doubleValue = 0;
            return false;
        }

        doubleValue = (double)scaled + (double)remainder / (double)d;
        return true;
    }
}