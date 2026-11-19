using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SystemPerformanceWidget;

internal static class SparklineHelper
{
    public static void Render(
        Canvas canvas,
        IReadOnlyList<double> values,
        double minVal,
        double maxVal,
        Color strokeColor,
        bool autoScale = false)
    {
        canvas.Children.Clear();

        double width = canvas.ActualWidth > 1 ? canvas.ActualWidth : canvas.Width;
        double height = canvas.ActualHeight > 1 ? canvas.ActualHeight : canvas.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 2 || height <= 2 || values.Count < 2)
            return;

        double actualMin = minVal;
        double actualMax = maxVal;
        if (autoScale)
        {
            double peak = 0;
            foreach (double v in values)
                if (v > peak) peak = v;
            actualMin = 0;
            actualMax = Math.Max(maxVal, peak * 1.15);
        }

        double range = Math.Max(1.0, actualMax - actualMin);
        double stepX = width / Math.Max(1, values.Count - 1);
        double topPad = 2.0;
        double bottomPad = 2.0;
        double usableHeight = Math.Max(2.0, height - topPad - bottomPad);

        var linePoints = new PointCollection(values.Count);
        var areaPoints = new PointCollection(values.Count + 2)
        {
            new Point(0, height)
        };

        for (int i = 0; i < values.Count; i++)
        {
            double normalized = Math.Clamp((values[i] - actualMin) / range, 0.0, 1.0);
            double x = i * stepX;
            double y = topPad + usableHeight * (1.0 - normalized);
            var pt = new Point(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);
        }

        areaPoints.Add(new Point((values.Count - 1) * stepX, height));

        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(115, strokeColor.R, strokeColor.G, strokeColor.B), 0.0));
        fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, strokeColor.R, strokeColor.G, strokeColor.B), 1.0));
        fillBrush.Freeze();

        var strokeBrush = new SolidColorBrush(strokeColor);
        strokeBrush.Freeze();

        var area = new Polygon
        {
            Points = areaPoints,
            Fill = fillBrush,
            IsHitTestVisible = false
        };

        var line = new Polyline
        {
            Points = linePoints,
            Stroke = strokeBrush,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };

        canvas.Children.Add(area);
        canvas.Children.Add(line);
    }
}
