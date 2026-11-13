using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace HardwareWidget;

public class WidgetSettings
{
    public double Opacity { get; set; } = 0.90;
    public double Scale { get; set; } = 1.0;
    public bool IsHorizontal { get; set; } = false;
    public bool MinimalistMode { get; set; } = false;
    public bool Topmost { get; set; } = true;
    public bool LockPosition { get; set; } = false;
    public bool StartWithWindows { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

public partial class MainWindow : Window
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly SolidColorBrush GreenBrush = CreateFrozenBrush(0, 230, 118);
    private static readonly SolidColorBrush YellowBrush = CreateFrozenBrush(255, 179, 0);
    private static readonly SolidColorBrush RedBrush = CreateFrozenBrush(255, 82, 82);
    private static readonly SolidColorBrush MutedBrush = CreateFrozenBrush(150, 160, 175);

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _topmostTimer;
    private readonly bool _isAdmin;
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _instanceFilePath;
    private readonly string _closeSignalPath;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private WidgetSettings _settings = new();
    private bool _isInitializing = true;
    private bool _closedBySignal = false;
    private volatile bool _isDragging;
    private IntPtr _hwnd;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();

        string projectDir = @"c:\Users\csolo\Documents\witget";
        _baseDir = Directory.Exists(projectDir) ? projectDir : AppDomain.CurrentDomain.BaseDirectory;
        _settingsPath = Path.Combine(_baseDir, "widget_settings.json");
        _instanceFilePath = Path.Combine(_baseDir, "active_instance.txt");
        _closeSignalPath = Path.Combine(_baseDir, "close.signal");

        // Registrar esta instancia como la única activa (cualquier instancia anterior se cerrará sola al detectarlo)
        try
        {
            if (File.Exists(_closeSignalPath)) File.Delete(_closeSignalPath);
            File.WriteAllText(_instanceFilePath, _instanceId);
        }
        catch { }

        _isAdmin = IsRunningAsAdministrator();
        if (_isAdmin)
        {
            AdminMenuItem.Visibility = Visibility.Collapsed;
        }

        LoadSettings();
        ConfigureAutoStart(_settings.StartWithWindows);

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true
        };

        // Timer cada 400ms: verifica instancia única y mantiene el widget sobre la Barra de Tareas
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            if (ShouldCloseForNewerInstance())
            {
                _closedBySignal = true;
                Close();
                return;
            }

            if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen)
            {
                ForceAboveTaskbar();
            }
        };

        _isInitializing = false;
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
        _hwnd = new WindowInteropHelper(this).Handle;

        if (_hwnd != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            ForceAboveTaskbar();
        }
    }

    private void ForceAboveTaskbar()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Topmost)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (Topmost)
        {
            ForceAboveTaskbar();
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<WidgetSettings>(json) ?? new WidgetSettings();
            }
        }
        catch
        {
            _settings = new WidgetSettings();
        }

        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }
        else
        {
            Left = SystemParameters.WorkArea.Width - 260;
            Top = 20;
        }

        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.15, 1.0);
        ScaleSlider.Value = Math.Clamp(_settings.Scale, 0.50, 2.0);
        Topmost = _settings.Topmost;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        LockPositionMenuItem.IsChecked = _settings.LockPosition;
        MinimalistMenuItem.IsChecked = _settings.MinimalistMode;
        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;

        ApplyMinimalistMode(_settings.MinimalistMode);
        ApplyOrientation(_settings.IsHorizontal);
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
            _settings.IsHorizontal = HorizontalLayoutMenuItem.IsChecked;
            _settings.MinimalistMode = MinimalistMenuItem.IsChecked;
            _settings.Topmost = Topmost;
            _settings.LockPosition = LockPositionMenuItem.IsChecked;
            _settings.StartWithWindows = StartWithWindowsMenuItem.IsChecked;
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
            string exePath = Path.Combine(_baseDir, @"bin\Release\net8.0-windows\HardwareWidget.exe");
            if (!File.Exists(exePath))
            {
                exePath = Environment.ProcessPath ?? "";
            }
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enable)
            {
                runKey?.SetValue("HardwareWidget", $"\"{exePath}\"");
            }
            else
            {
                runKey?.DeleteValue("HardwareWidget", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private void StartWithWindowsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAutoStart(StartWithWindowsMenuItem.IsChecked);
        SaveSettings();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }

        ForceAboveTaskbar();
        _topmostTimer.Start();

        _ = Task.Run(() => SensorLoopAsync(_cts.Token));
    }

    private async Task SensorLoopAsync(CancellationToken token)
    {
        try
        {
            _computer.Open();
        }
        catch
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        do
        {
            if (!_isDragging && !_closedBySignal)
            {
                ReadSensorsAndDispatch();
            }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private void ReadSensorsAndDispatch()
    {
        try
        {
            _computer.Accept(_updateVisitor);

            float? cpuTemp = null;
            float? cpuLoad = null;
            string cpuName = "AMD CPU";

            float? gpuTemp = null;
            float? gpuLoad = null;
            string gpuName = "NVIDIA GPU";

            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuName = CleanHardwareName(hardware.Name);

                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                        {
                            if (sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                cpuTemp == null)
                            {
                                cpuTemp = sensor.Value.Value;
                            }
                        }
                        else if (sensor.SensorType == SensorType.Load &&
                                 sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) &&
                                 sensor.Value.HasValue)
                        {
                            cpuLoad = sensor.Value.Value;
                        }
                    }
                }

                if (hardware.HardwareType == HardwareType.Motherboard && cpuTemp == null)
                {
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        foreach (ISensor sensor in sub.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature &&
                                sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) &&
                                sensor.Value.HasValue &&
                                sensor.Value.Value is > 10 and < 120)
                            {
                                cpuTemp = sensor.Value.Value;
                            }
                        }
                    }
                }

                if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    bool isNvidia = hardware.HardwareType == HardwareType.GpuNvidia;
                    if (isNvidia || gpuTemp == null)
                    {
                        gpuName = CleanHardwareName(hardware.Name);

                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpuTemp == null)
                                {
                                    gpuTemp = sensor.Value.Value;
                                }
                            }
                            else if (sensor.SensorType == SensorType.Load &&
                                     sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                                     sensor.Value.HasValue)
                            {
                                gpuLoad = sensor.Value.Value;
                            }
                        }
                    }
                }
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isDragging || _closedBySignal) return;

                CpuNameText.Text = cpuName;
                CpuLoadText.Text = cpuLoad.HasValue ? $"Uso: {cpuLoad.Value:0}%" : "Uso: --%";
                if (cpuTemp.HasValue && cpuTemp.Value > 0)
                {
                    CpuTempText.Text = $"{cpuTemp.Value:0}°C";
                    CpuTempText.Foreground = GetTemperatureBrush(cpuTemp.Value);
                    AdminWarningButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CpuTempText.Text = "--°C";
                    CpuTempText.Foreground = MutedBrush;
                    if (!_isAdmin)
                    {
                        AdminWarningButton.Visibility = Visibility.Visible;
                    }
                }

                GpuNameText.Text = gpuName;
                GpuLoadText.Text = gpuLoad.HasValue ? $"Uso: {gpuLoad.Value:0}%" : "Uso: --%";
                if (gpuTemp.HasValue && gpuTemp.Value > 0)
                {
                    GpuTempText.Text = $"{gpuTemp.Value:0}°C";
                    GpuTempText.Foreground = GetTemperatureBrush(gpuTemp.Value);
                }
                else
                {
                    GpuTempText.Text = "--°C";
                    GpuTempText.Foreground = MutedBrush;
                }
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private static string CleanHardwareName(string rawName)
    {
        return rawName
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
            .Replace("12-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("16-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("8-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("6-Core", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static SolidColorBrush GetTemperatureBrush(float temp)
    {
        if (temp < 65) return GreenBrush;
        if (temp < 80) return YellowBrush;
        return RedBrush;
    }

    // --- MODO MINIMALISTA ---
    private void MinimalistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyMinimalistMode(MinimalistMenuItem.IsChecked);
        SaveSettings();
    }

    private void ApplyMinimalistMode(bool isMinimalist)
    {
        if (CpuTitleText == null || GpuTitleText == null) return;

        if (isMinimalist)
        {
            CpuTitleText.Text = "CPU";
            GpuTitleText.Text = "GPU";
            CpuTitleText.FontSize = 15;
            GpuTitleText.FontSize = 15;

            CpuNameText.Visibility = Visibility.Collapsed;
            CpuLoadText.Visibility = Visibility.Collapsed;
            GpuNameText.Visibility = Visibility.Collapsed;
            GpuLoadText.Visibility = Visibility.Collapsed;

            CpuCard.Width = double.NaN;
            GpuCard.Width = double.NaN;
            CpuCard.MinWidth = 120;
            GpuCard.MinWidth = 120;
            CpuCard.Padding = new Thickness(12, 6, 12, 6);
            GpuCard.Padding = new Thickness(12, 6, 12, 6);
        }
        else
        {
            CpuTitleText.Text = "CPU (AMD)";
            GpuTitleText.Text = "GPU (NVIDIA)";
            CpuTitleText.FontSize = 11;
            GpuTitleText.FontSize = 11;

            CpuNameText.Visibility = Visibility.Visible;
            CpuLoadText.Visibility = Visibility.Visible;
            GpuNameText.Visibility = Visibility.Visible;
            GpuLoadText.Visibility = Visibility.Visible;

            CpuCard.Width = 215;
            GpuCard.Width = 215;
            CpuCard.MinWidth = 0;
            GpuCard.MinWidth = 0;
            CpuCard.Padding = new Thickness(12, 10, 12, 10);
            GpuCard.Padding = new Thickness(12, 10, 12, 10);
        }
    }

    // --- OPACIDAD (SLIDER) ---
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

    // --- TAMAÑO / ESCALA (SLIDER) ---
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

    // --- ORIENTACIÓN (VERTICAL / HORIZONTAL) ---
    private void VerticalLayout_Click(object sender, RoutedEventArgs e)
    {
        ApplyOrientation(isHorizontal: false);
        SaveSettings();
    }

    private void HorizontalLayout_Click(object sender, RoutedEventArgs e)
    {
        ApplyOrientation(isHorizontal: true);
        SaveSettings();
    }

    private void ApplyOrientation(bool isHorizontal)
    {
        if (CardsPanel == null || GpuCard == null) return;

        if (isHorizontal)
        {
            CardsPanel.Orientation = Orientation.Horizontal;
            GpuCard.Margin = new Thickness(8, 0, 0, 0);
            HorizontalLayoutMenuItem.IsChecked = true;
            VerticalLayoutMenuItem.IsChecked = false;
        }
        else
        {
            CardsPanel.Orientation = Orientation.Vertical;
            GpuCard.Margin = new Thickness(0, 8, 0, 0);
            VerticalLayoutMenuItem.IsChecked = true;
            HorizontalLayoutMenuItem.IsChecked = false;
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            ProcessStartInfo startInfo = new()
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                startInfo.Arguments = $"\"{dllPath}\"";
            }

            Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LockPositionMenuItem.IsChecked != true && e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            try
            {
                DragMove();
            }
            finally
            {
                _isDragging = false;
            }
            ForceAboveTaskbar();
            SaveSettings();
        }
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            Topmost = item.IsChecked;
            ForceAboveTaskbar();
            SaveSettings();
        }
    }

    private void LockPositionMenuItem_Click(object sender, RoutedEventArgs e) => SaveSettings();

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
        _topmostTimer.Stop();
        _cts.Cancel();
        try { _computer.Close(); } catch { }
    }
}
