using System.Globalization;

namespace OtimizadorPC.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Formata um tamanho em bytes usando base 1024 (ex.: 1536 → "1,5 KB").</summary>
    public static string Format(long bytes, int decimals = 1)
    {
        if (bytes < 0) return "-" + Format(-bytes, decimals);
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : "0." + new string('#', Math.Max(1, decimals));
        return value.ToString(format, PtBr) + " " + Units[unit];
    }

    public static double ToGb(long bytes) => bytes / 1024d / 1024d / 1024d;
}
