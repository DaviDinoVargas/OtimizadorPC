namespace OtimizadorPC.Core.DiskAnalysis;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Layout "squarified" (Bruls, Huizing e van Wijk): retângulos com proporção próxima de um
/// quadrado, o que deixa o mapa legível. Os valores devem vir ordenados do maior para o menor.
/// </summary>
public static class Treemap
{
    public static IReadOnlyList<TreemapRect> Squarify(IReadOnlyList<double> values, TreemapRect bounds)
    {
        var result = new TreemapRect[values.Count];
        var total = values.Sum();
        if (values.Count == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        // Normaliza os valores para a área disponível
        var scale = bounds.Area / total;
        var areas = values.Select(v => Math.Max(0, v) * scale).ToArray();

        var rect = bounds;
        var start = 0;
        while (start < areas.Length)
        {
            var side = Math.Min(rect.Width, rect.Height);
            var end = start;
            var rowSum = 0.0;
            var worst = double.MaxValue;

            // Cresce a linha enquanto a pior proporção melhorar
            while (end < areas.Length)
            {
                var candidateSum = rowSum + areas[end];
                var candidateWorst = Worst(areas, start, end, candidateSum, side);
                if (end > start && candidateWorst > worst) break;
                rowSum = candidateSum;
                worst = candidateWorst;
                end++;
            }

            // Posiciona a linha ao longo do lado menor
            var horizontal = rect.Width >= rect.Height; // linha vertical na esquerda quando o retângulo é largo
            var thickness = side == 0 ? 0 : rowSum / side;
            var offset = 0.0;
            for (var i = start; i < end; i++)
            {
                var length = thickness == 0 ? 0 : areas[i] / thickness;
                result[i] = horizontal
                    ? new TreemapRect(rect.X, rect.Y + offset, thickness, length)
                    : new TreemapRect(rect.X + offset, rect.Y, length, thickness);
                offset += length;
            }

            rect = horizontal
                ? new TreemapRect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new TreemapRect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            start = end;
        }
        return result;
    }

    private static double Worst(double[] areas, int start, int endInclusive, double sum, double side)
    {
        if (sum <= 0 || side <= 0) return double.MaxValue;
        double max = 0, min = double.MaxValue;
        for (var i = start; i <= endInclusive; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * Math.Max(min, 1e-9)));
    }
}
