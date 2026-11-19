using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WeatherWidget;

public class WeatherSettings
{
    public bool AutoLocation { get; set; } = true;
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool UseFahrenheit { get; set; } = false;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool Topmost { get; set; } = false;
    public bool LockPosition { get; set; } = false;
    public bool StartWithWindows { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _instanceTimer;
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _instanceFilePath;
    private readonly string _closeSignalPath;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private WeatherSettings _settings = new();
    private WeatherData? _lastWeather;
    private bool _isInitializing = true;
    private bool _closedBySignal = false;
    private bool _isRefreshing = false;

    public MainWindow()
    {
        InitializeComponent();

        _baseDir = ResolveBaseDirectory();
        _settingsPath = Path.Combine(_baseDir, "weather_settings.json");
        _instanceFilePath = Path.Combine(_baseDir, "active_instance.txt");
        _closeSignalPath = Path.Combine(_baseDir, "close.signal");

        try
        {
            if (File.Exists(_closeSignalPath)) File.Delete(_closeSignalPath);
            File.WriteAllText(_instanceFilePath, _instanceId);
        }
        catch { }

        IconHost.Child = WeatherIconFactory.CreateIcon(2, isDay: true, size: 56);

        LoadSettings();
        ConfigureAutoStart(_settings.StartWithWindows);

        _instanceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _instanceTimer.Tick += (_, _) =>
        {
            if (ShouldCloseForNewerInstance())
            {
                _closedBySignal = true;
                Close();
            }
        };

        _isInitializing = false;
    }

    private static string ResolveBaseDirectory()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(current);
        for (int i = 0; i < 7 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WeatherWidget.csproj")) ||
                File.Exists(Path.Combine(dir.FullName, "weather_settings.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return current;
    }

    private string ResolveExePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            File.Exists(processPath) &&
            !processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }

        string appDirExe = Path.Combine(AppContext.BaseDirectory, "WeatherWidget.exe");
        if (File.Exists(appDirExe)) return Path.GetFullPath(appDirExe);

        string releaseExe = Path.Combine(_baseDir, @"bin\Release\net8.0-windows\WeatherWidget.exe");
        if (File.Exists(releaseExe)) return Path.GetFullPath(releaseExe);

        return string.Empty;
    }

    private bool ShouldCloseForNewerInstance()
    {
        try
        {
            if (File.Exists(_closeSignalPath))
            {
                try { File.Delete(_closeSignalPath); } catch { }
                return true;
            }

            if (File.Exists(_instanceFilePath))
            {
                string activeId = File.ReadAllText(_instanceFilePath).Trim();
                if (!string.IsNullOrEmpty(activeId) && activeId != _instanceId)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<WeatherSettings>(json) ?? new WeatherSettings();
            }
        }
        catch
        {
            _settings = new WeatherSettings();
        }

        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }
        else
        {
            Left = (SystemParameters.WorkArea.Width - 348) / 2.0;
            Top = SystemParameters.WorkArea.Height - 200;
        }

        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.25, 1.0);
        ScaleSlider.Value = Math.Clamp(_settings.Scale, 0.60, 1.60);
        Topmost = _settings.Topmost;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        LockPositionMenuItem.IsChecked = _settings.LockPosition;
        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;
        CelsiusMenuItem.IsChecked = !_settings.UseFahrenheit;
        FahrenheitMenuItem.IsChecked = _settings.UseFahrenheit;

        if (!string.IsNullOrWhiteSpace(_settings.City))
        {
            CityText.Text = _settings.City;
        }

        ApplyOpacity(OpacitySlider.Value);
        ApplyScale(ScaleSlider.Value);
    }

    private void SaveSettings()
    {
        if (_isInitializing || _closedBySignal) return;
        try
        {
            _settings.Opacity = OpacitySlider.Value;
            _settings.Scale = ScaleSlider.Value;
            _settings.Topmost = Topmost;
            _settings.LockPosition = LockPositionMenuItem.IsChecked;
            _settings.StartWithWindows = StartWithWindowsMenuItem.IsChecked;
            _settings.UseFahrenheit = FahrenheitMenuItem.IsChecked;
            _settings.Left = Left;
            _settings.Top = Top;

            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    private void ConfigureAutoStart(bool enable)
    {
        try
        {
            string exePath = ResolveExePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (enable)
            {
                runKey?.SetValue("WeatherWidget", $"\"{exePath}\"");
            }
            else
            {
                runKey?.DeleteValue("WeatherWidget", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }

        _instanceTimer.Start();
        _ = RunWeatherLoopAsync(_cts.Token);
    }

    private async Task RunWeatherLoopAsync(CancellationToken token)
    {
        await RefreshWeatherAsync(token);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await RefreshWeatherAsync(token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshWeatherAsync(CancellationToken token)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            LocationInfo? location = null;

            if (!_settings.AutoLocation &&
                !string.IsNullOrWhiteSpace(_settings.City) &&
                _settings.Latitude.HasValue &&
                _settings.Longitude.HasValue)
            {
                location = new LocationInfo(_settings.City, _settings.Latitude.Value, _settings.Longitude.Value);
            }
            else
            {
                location = await WeatherService.DetectLocationByIpAsync(token);
                if (location != null)
                {
                    _settings.City = location.City;
                    _settings.Latitude = location.Latitude;
                    _settings.Longitude = location.Longitude;
                    SaveSettings();
                }
                else if (!string.IsNullOrWhiteSpace(_settings.City) &&
                         _settings.Latitude.HasValue &&
                         _settings.Longitude.HasValue)
                {
                    location = new LocationInfo(_settings.City, _settings.Latitude.Value, _settings.Longitude.Value);
                }
            }

            if (location == null)
            {
                Dispatcher.Invoke(() =>
                {
                    CityText.Text = "Sin ubicación";
                    SummaryText.Text = "Usa ••• para elegir tu ciudad";
                });
                return;
            }

            WeatherData weather = await WeatherService.FetchWeatherAsync(location, token);
            _lastWeather = weather;

            Dispatcher.Invoke(() => RenderWeather(weather));
        }
        catch (OperationCanceledException) { }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                if (_lastWeather == null)
                {
                    SummaryText.Text = "Sin conexión al clima";
                }
            });
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private double ConvertTemp(double tempC) =>
        _settings.UseFahrenheit ? (tempC * 9.0 / 5.0) + 32.0 : tempC;

    private string UnitSuffix => _settings.UseFahrenheit ? "°F" : "°C";

    private void RenderWeather(WeatherData weather)
    {
        CityText.Text = weather.City;
        TempValueText.Text = $"{Math.Round(ConvertTemp(weather.TemperatureC), MidpointRounding.AwayFromZero):0}";
        TempUnitText.Text = UnitSuffix;
        SummaryText.Text = weather.SummaryText;
        IconHost.Child = WeatherIconFactory.CreateIcon(weather.WeatherCode, weather.IsDay, size: 56);

        FeelsLikeText.Text = $"{Math.Round(ConvertTemp(weather.FeelsLikeC), MidpointRounding.AwayFromZero):0}°";
        HumidityText.Text = $"{weather.HumidityPercent}%";
        WindText.Text = $"{Math.Round(weather.WindKmh):0} km/h";
        UpdatedAtText.Text = $"Actualizado {weather.UpdatedAt:HH:mm}";

        // Próximas horas
        HourlyGrid.Children.Clear();
        foreach (var item in weather.Hourly)
        {
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };

            stack.Children.Add(new TextBlock
            {
                Text = item.HourLabel,
                Foreground = new SolidColorBrush(Color.FromRgb(184, 196, 210)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var iconWrap = new Border
            {
                Margin = new Thickness(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = WeatherIconFactory.CreateIcon(item.WeatherCode, item.IsDay, size: 24)
            };
            stack.Children.Add(iconWrap);

            stack.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(ConvertTemp(item.TemperatureC), MidpointRounding.AwayFromZero):0}°",
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            HourlyGrid.Children.Add(stack);
        }

        // Próximos días
        DailyStack.Children.Clear();
        foreach (var day in weather.Daily)
        {
            var row = new Grid { Margin = new Thickness(0, 2.5, 0, 2.5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dayLabel = new TextBlock
            {
                Text = day.DayLabel,
                Foreground = new SolidColorBrush(Color.FromRgb(235, 240, 246)),
                FontSize = 11.5,
                FontWeight = day.DayLabel == "Hoy" ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dayLabel, 0);
            row.Children.Add(dayLabel);

            var icon = WeatherIconFactory.CreateIcon(day.WeatherCode, isDay: true, size: 22);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);

            var tempsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            tempsPanel.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(ConvertTemp(day.MaxTempC), MidpointRounding.AwayFromZero):0}°",
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            });
            tempsPanel.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(ConvertTemp(day.MinTempC), MidpointRounding.AwayFromZero):0}°",
                Foreground = new SolidColorBrush(Color.FromRgb(162, 176, 192)),
                FontSize = 11.5
            });
            Grid.SetColumn(tempsPanel, 2);
            row.Children.Add(tempsPanel);

            DailyStack.Children.Add(row);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        WidgetContextMenu.PlacementTarget = MoreButton;
        WidgetContextMenu.IsOpen = true;
    }

    private void ForecastToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool show = FullForecastPanel.Visibility != Visibility.Visible;
        FullForecastPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ForecastToggleButton.Content = show ? "Ocultar pronóstico" : "Ver pronóstico completo";
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshWeatherAsync(_cts.Token);
    }

    private void ChangeCityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CitySearchPanel.Visibility = Visibility.Visible;
        CityInputBox.Text = _settings.City ?? string.Empty;
        CityInputBox.Focus();
        CityInputBox.SelectAll();
    }

    private async void ApplyCityButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAndApplyCityAsync();
    }

    private async void CityInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchAndApplyCityAsync();
        }
        else if (e.Key == Key.Escape)
        {
            CitySearchPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelCityButton_Click(object sender, RoutedEventArgs e)
    {
        CitySearchPanel.Visibility = Visibility.Collapsed;
    }

    private async Task SearchAndApplyCityAsync()
    {
        string query = CityInputBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        SummaryText.Text = "Buscando ciudad...";
        try
        {
            LocationInfo? found = await WeatherService.GeocodeCityAsync(query, _cts.Token);
            if (found == null)
            {
                SummaryText.Text = "Ciudad no encontrada";
                return;
            }

            _settings.AutoLocation = false;
            _settings.City = found.City;
            _settings.Latitude = found.Latitude;
            _settings.Longitude = found.Longitude;
            SaveSettings();

            CitySearchPanel.Visibility = Visibility.Collapsed;
            await RefreshWeatherAsync(_cts.Token);
        }
        catch
        {
            SummaryText.Text = "Error al buscar ciudad";
        }
    }

    private void AutoLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoLocation = true;
        CitySearchPanel.Visibility = Visibility.Collapsed;
        SaveSettings();
        _ = RefreshWeatherAsync(_cts.Token);
    }

    private void CelsiusMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseFahrenheit = false;
        CelsiusMenuItem.IsChecked = true;
        FahrenheitMenuItem.IsChecked = false;
        SaveSettings();
        if (_lastWeather != null) RenderWeather(_lastWeather);
    }

    private void FahrenheitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseFahrenheit = true;
        CelsiusMenuItem.IsChecked = false;
        FahrenheitMenuItem.IsChecked = true;
        SaveSettings();
        if (_lastWeather != null) RenderWeather(_lastWeather);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyOpacity(e.NewValue);
        SaveSettings();
    }

    private void ApplyOpacity(double opacity)
    {
        if (RootContainer == null) return;
        RootContainer.Opacity = opacity;
        if (OpacityValueText != null)
            OpacityValueText.Text = $"{opacity * 100:0}%";
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyScale(e.NewValue);
        SaveSettings();
    }

    private void ApplyScale(double scale)
    {
        if (WidgetScaleTransform == null) return;
        WidgetScaleTransform.ScaleX = scale;
        WidgetScaleTransform.ScaleY = scale;
        if (ScaleValueText != null)
            ScaleValueText.Text = $"{scale * 100:0}%";
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        SaveSettings();
    }

    private void LockPositionMenuItem_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void StartWithWindowsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAutoStart(StartWithWindowsMenuItem.IsChecked);
        SaveSettings();
    }

    private void OpenMsnWeather_Click(object sender, RoutedEventArgs e)
    {
        string city = Uri.EscapeDataString(_settings.City ?? string.Empty);
        string url = string.IsNullOrEmpty(city)
            ? "https://www.msn.com/es-xl/clima"
            : $"https://www.msn.com/es-xl/clima/pronostico/in-{city}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LockPositionMenuItem.IsChecked != true && e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                SaveSettings();
            }
            catch { }
        }
    }

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveSettings();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _instanceTimer.Stop();
        _cts.Cancel();
    }
}
