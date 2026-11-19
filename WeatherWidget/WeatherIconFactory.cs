using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WeatherWidget;

public static class WeatherIconFactory
{
    public static UIElement CreateIcon(int weatherCode, bool isDay, double size = 56)
    {
        var canvas = new Canvas
        {
            Width = 64,
            Height = 64
        };

        switch (weatherCode)
        {
            case 0: // Despejado
                if (isDay)
                    AddSun(canvas, 10, 10, 44);
                else
                    AddMoon(canvas, 13, 11, 40);
                break;

            case 1: // Mayormente despejado
            case 2: // Parcialmente nublado (idéntico al ícono de la foto de Windows 11)
                if (isDay)
                    AddSun(canvas, 8, 6, 36);
                else
                    AddMoon(canvas, 10, 6, 32);
                AddCloud(canvas, 12, 24, 44, 28, isDark: false);
                break;

            case 3: // Nublado
                AddCloud(canvas, 16, 12, 38, 24, isDark: true);
                AddCloud(canvas, 8, 24, 48, 28, isDark: false);
                break;

            case 45:
            case 48: // Niebla
                AddCloud(canvas, 10, 14, 44, 26, isDark: false);
                AddFogBars(canvas);
                break;

            case 51:
            case 53:
            case 55:
            case 56:
            case 57: // Llovizna
                if (isDay)
                    AddSun(canvas, 10, 4, 30);
                AddCloud(canvas, 10, 18, 44, 26, isDark: false);
                AddRainDrops(canvas, count: 2);
                break;

            case 61:
            case 63:
            case 65:
            case 66:
            case 67:
            case 80:
            case 81:
            case 82: // Lluvia / chubascos
                AddCloud(canvas, 10, 14, 44, 27, isDark: true);
                AddRainDrops(canvas, count: 3);
                break;

            case 71:
            case 73:
            case 75:
            case 77:
            case 85:
            case 86: // Nieve
                AddCloud(canvas, 10, 14, 44, 26, isDark: false);
                AddSnowDots(canvas);
                break;

            case 95:
            case 96:
            case 99: // Tormenta eléctrica
                AddCloud(canvas, 10, 10, 44, 27, isDark: true);
                AddLightningBolt(canvas);
                AddRainDrops(canvas, count: 2);
                break;

            default:
                if (isDay)
                    AddSun(canvas, 8, 6, 36);
                else
                    AddMoon(canvas, 10, 6, 32);
                AddCloud(canvas, 12, 24, 44, 28, isDark: false);
                break;
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    private static void AddSun(Canvas canvas, double left, double top, double diameter)
    {
        var sunBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.2, 0.0),
            EndPoint = new Point(0.8, 1.0)
        };
        sunBrush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 213, 64), 0.0));
        sunBrush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 149, 24), 0.55));
        sunBrush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 96, 18), 1.0));
        sunBrush.Freeze();

        var sun = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = sunBrush
        };
        Canvas.SetLeft(sun, left);
        Canvas.SetTop(sun, top);
        canvas.Children.Add(sun);
    }

    private static void AddMoon(Canvas canvas, double left, double top, double size)
    {
        var moonBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.1, 0.0),
            EndPoint = new Point(0.9, 1.0)
        };
        moonBrush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 238, 148), 0.0));
        moonBrush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 198, 66), 1.0));
        moonBrush.Freeze();

        var path = new Path
        {
            Fill = moonBrush,
            Data = Geometry.Parse("M 26,4 A 18,18 0 1 0 42,29 A 14,14 0 1 1 26,4 Z"),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform
        };
        Canvas.SetLeft(path, left);
        Canvas.SetTop(path, top);
        canvas.Children.Add(path);
    }

    private static void AddCloud(Canvas canvas, double left, double top, double width, double height, bool isDark)
    {
        var cloudBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };

        if (isDark)
        {
            cloudBrush.GradientStops.Add(new GradientStop(Color.FromRgb(205, 218, 232), 0.0));
            cloudBrush.GradientStops.Add(new GradientStop(Color.FromRgb(142, 165, 190), 1.0));
        }
        else
        {
            cloudBrush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 255), 0.0));
            cloudBrush.GradientStops.Add(new GradientStop(Color.FromRgb(219, 236, 252), 0.65));
            cloudBrush.GradientStops.Add(new GradientStop(Color.FromRgb(176, 210, 244), 1.0));
        }
        cloudBrush.Freeze();

        var cloud = new Path
        {
            Fill = cloudBrush,
            Data = Geometry.Parse("M 16,32 H 44 A 10,10 0 0 0 45,12 A 14,14 0 0 0 18,10 A 11,11 0 0 0 16,32 Z"),
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        };
        Canvas.SetLeft(cloud, left);
        Canvas.SetTop(cloud, top);
        canvas.Children.Add(cloud);
    }

    private static void AddRainDrops(Canvas canvas, int count)
    {
        var dropBrush = new LinearGradientBrush(
            Color.FromRgb(110, 200, 255),
            Color.FromRgb(33, 145, 243),
            new Point(0, 0),
            new Point(0, 1));
        dropBrush.Freeze();

        double[] xs = count == 2 ? [23, 37] : [18, 30, 42];
        double[] ys = count == 2 ? [46, 48] : [45, 48, 45];

        for (int i = 0; i < xs.Length; i++)
        {
            var drop = new Rectangle
            {
                Width = 4.2,
                Height = 11,
                RadiusX = 2.1,
                RadiusY = 2.1,
                Fill = dropBrush,
                RenderTransform = new RotateTransform(18)
            };
            Canvas.SetLeft(drop, xs[i]);
            Canvas.SetTop(drop, ys[i]);
            canvas.Children.Add(drop);
        }
    }

    private static void AddLightningBolt(Canvas canvas)
    {
        var boltBrush = new SolidColorBrush(Color.FromRgb(255, 214, 51));
        boltBrush.Freeze();

        var bolt = new Polygon
        {
            Fill = boltBrush,
            Points = new PointCollection
            {
                new(32, 34),
                new(24, 48),
                new(31, 48),
                new(27, 60),
                new(41, 44),
                new(34, 44)
            }
        };
        canvas.Children.Add(bolt);
    }

    private static void AddSnowDots(Canvas canvas)
    {
        var snowBrush = new SolidColorBrush(Color.FromRgb(230, 244, 255));
        snowBrush.Freeze();

        (double x, double y)[] dots = [(20, 46), (31, 50), (42, 46), (26, 55), (37, 55)];
        foreach (var (x, y) in dots)
        {
            var flake = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = snowBrush
            };
            Canvas.SetLeft(flake, x);
            Canvas.SetTop(flake, y);
            canvas.Children.Add(flake);
        }
    }

    private static void AddFogBars(Canvas canvas)
    {
        var fogBrush = new SolidColorBrush(Color.FromArgb(215, 214, 226, 240));
        fogBrush.Freeze();

        (double x, double y, double w)[] bars = [(14, 44, 36), (18, 50, 28), (12, 56, 38)];
        foreach (var (x, y, w) in bars)
        {
            var bar = new Rectangle
            {
                Width = w,
                Height = 3.5,
                RadiusX = 1.75,
                RadiusY = 1.75,
                Fill = fogBrush
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y);
            canvas.Children.Add(bar);
        }
    }
}
