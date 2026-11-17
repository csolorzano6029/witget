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
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace HardwareWidget;

public class WidgetSettings
{
    public double Opacity { get; set; } = 0.90;
    public double Scale { get; set; } = 1.0;
    public bool IsHorizontal { get; set; } = false;
    public bool MinimalistMode { get; set; } = false;
    public bool ShowFps { get; set; } = true;
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
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint GW_HWNDPREV = 3;
    private const uint GA_ROOT = 2;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    private const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private static readonly SolidColorBrush GreenBrush = CreateFrozenBrush(0, 230, 118);
    private static readonly SolidColorBrush YellowBrush = CreateFrozenBrush(255, 179, 0);
    private static readonly SolidColorBrush RedBrush = CreateFrozenBrush(255, 82, 82);
    private static readonly SolidColorBrush MutedBrush = CreateFrozenBrush(150, 160, 175);

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly FpsMonitor _fpsMonitor = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _topmostTimer;
    private readonly bool _isAdmin;
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _instanceFilePath;
    private readonly string _closeSignalPath;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly char[] _classNameBuffer = new char[64];
    private WidgetSettings _settings = new();
    private string _cpuTitleLabel = "CPU";
    private string _gpuTitleLabel = "GPU";
    private bool _isInitializing = true;
    private bool _closedBySignal = false;
    private int _instanceCheckTick;
    private volatile bool _wasPawnIoInstalled;
    private volatile bool _isInstallingPawnIo;
    private volatile bool _showFps = true;
    private volatile bool _isDragging;
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();

        _baseDir = ElevatedTaskHelper.ResolveBaseDirectory();
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

        _isAdmin = ElevatedTaskHelper.IsRunningAsAdministrator();
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

        // Timer ligero: verifica instancia única cada ~400ms y asegura el orden Z sobre la Barra de Tareas solo si hace falta
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            if (++_instanceCheckTick >= 4)
            {
                _instanceCheckTick = 0;
                if (ShouldCloseForNewerInstance())
                {
                    _closedBySignal = true;
                    Close();
                    return;
                }
            }

            if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && IsTaskbarAboveWidget())
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

            _winEventDelegate = OnWinEvent;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_CAPTURESTART,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!Topmost || _isDragging || _closedBySignal || WidgetContextMenu.IsOpen || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType is not (EVENT_SYSTEM_FOREGROUND or EVENT_SYSTEM_MENUPOPUPSTART or EVENT_SYSTEM_CAPTURESTART))
        {
            return;
        }

        if (IsTaskbarAboveWidget())
        {
            ForceAboveTaskbar();
        }
        else if (IsTaskbarEventWindow(hwnd))
        {
            // Si la Barra de Tareas recibió clic/foco pero aún está subiendo en el Z-order
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && IsTaskbarAboveWidget())
                {
                    ForceAboveTaskbar();
                }
            });
        }
    }

    private bool IsTaskbarAboveWidget()
    {
        if (_hwnd == IntPtr.Zero) return false;

        IntPtr current = GetWindow(_hwnd, GW_HWNDPREV);
        for (int i = 0; i < 40 && current != IntPtr.Zero; i++)
        {
            if (IsTaskbarWindow(current))
            {
                return true;
            }
            current = GetWindow(current, GW_HWNDPREV);
        }
        return false;
    }

    private bool IsTaskbarEventWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsTaskbarWindow(hwnd)) return true;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        return root != IntPtr.Zero && root != hwnd && IsTaskbarWindow(root);
    }

    private bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        int len = GetClassName(hwnd, _classNameBuffer, _classNameBuffer.Length);
        if (len <= 0) return false;

        ReadOnlySpan<char> className = _classNameBuffer.AsSpan(0, len);
        return className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
               className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal);
    }

    private void ForceAboveTaskbar()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Topmost)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        else
        {
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen)
        {
            ForceAboveTaskbar();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && IsTaskbarAboveWidget())
                {
                    ForceAboveTaskbar();
                }
            });
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
        ShowFpsMenuItem.IsChecked = _settings.ShowFps;
        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;

        ApplyMinimalistMode(_settings.MinimalistMode);
        ApplyShowFps(_settings.ShowFps, manageMonitor: false);
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
            _settings.ShowFps = ShowFpsMenuItem.IsChecked;
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
            string exePath = ElevatedTaskHelper.ResolveExePath(_baseDir);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (_isAdmin && ElevatedTaskHelper.RegisterOrUpdateTask(exePath, _baseDir, enable))
            {
                // Eliminar la entrada Run clásica para evitar un segundo arranque sin permisos de Administrador
                runKey?.DeleteValue("HardwareWidget", throwOnMissingValue: false);
                return;
            }

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

        if (_showFps)
        {
            _fpsMonitor.Start();
        }

        _ = Task.Run(() => SensorLoopAsync(_cts.Token));
    }

    private async Task SensorLoopAsync(CancellationToken token)
    {
        try
        {
            _wasPawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
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
                bool pawnIoNow = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
                if (!_wasPawnIoInstalled && pawnIoNow)
                {
                    _wasPawnIoInstalled = true;
                    try
                    {
                        _computer.Close();
                        _computer.Open();
                    }
                    catch { }
                }

                ReadSensorsAndDispatch(pawnIoNow);
            }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private void ReadSensorsAndDispatch(bool pawnIoInstalled)
    {
        try
        {
            _computer.Accept(_updateVisitor);

            float? cpuTemp = null;
            float? cpuLoad = null;
            string cpuName = "Detectando...";
            string cpuTitle = _cpuTitleLabel;

            float? gpuTemp = null;
            float? gpuLoad = null;
            string gpuName = "Detectando...";
            string gpuTitle = _gpuTitleLabel;
            int bestGpuPriority = 0;

            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuTitle = ResolveCpuTitle(hardware.Name);
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
                    int gpuPriority = hardware.HardwareType switch
                    {
                        HardwareType.GpuNvidia => 3,
                        HardwareType.GpuAmd => 2,
                        HardwareType.GpuIntel => 1,
                        _ => 0
                    };

                    if (gpuPriority > bestGpuPriority || (gpuPriority == bestGpuPriority && gpuTemp == null))
                    {
                        bestGpuPriority = gpuPriority;
                        gpuTitle = hardware.HardwareType switch
                        {
                            HardwareType.GpuNvidia => "GPU (NVIDIA)",
                            HardwareType.GpuAmd => "GPU (AMD)",
                            HardwareType.GpuIntel => "GPU (INTEL)",
                            _ => "GPU"
                        };
                        gpuName = CleanHardwareName(hardware.Name);
                        gpuTemp = null;
                        gpuLoad = null;

                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                    sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                                    gpuTemp == null)
                                {
                                    gpuTemp = sensor.Value.Value;
                                }
                            }
                            else if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
                            {
                                if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                    sensor.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase) ||
                                    gpuLoad == null)
                                {
                                    gpuLoad = sensor.Value.Value;
                                }
                            }
                        }
                    }
                }
            }

            _cpuTitleLabel = cpuTitle;
            _gpuTitleLabel = gpuTitle;

            FpsSnapshot? fpsSnapshot = _showFps ? _fpsMonitor.GetCurrentSnapshot() : null;

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isDragging || _closedBySignal) return;

                if (!MinimalistMenuItem.IsChecked)
                {
                    CpuTitleText.Text = cpuTitle;
                    GpuTitleText.Text = gpuTitle;
                }

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
                        AdminWarningButton.Tag = "admin";
                        AdminWarningButton.Content = "🛡️ Clic para activar temp. CPU (Admin)";
                        AdminWarningButton.Visibility = Visibility.Visible;
                    }
                    else if (!pawnIoInstalled)
                    {
                        AdminWarningButton.Tag = "pawnio";
                        if (!_isInstallingPawnIo)
                        {
                            AdminWarningButton.Content = "⬇️ Clic para instalar driver PawnIO (Temp. CPU)";
                        }
                        AdminWarningButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        AdminWarningButton.Visibility = Visibility.Collapsed;
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

                if (_showFps && fpsSnapshot.HasValue)
                {
                    FpsAppText.Text = fpsSnapshot.Value.AppName;
                    if (fpsSnapshot.Value.Fps is double fps)
                    {
                        FpsValueText.Text = $"{fps:0}";
                        FpsValueText.Foreground = GetFpsBrush(fps);
                        FpsFrameTimeText.Text = fpsSnapshot.Value.FrameTimeMs is double ft
                            ? $"Frame: {ft:0.0} ms"
                            : "Frame: -- ms";
                    }
                    else
                    {
                        FpsValueText.Text = "--";
                        FpsValueText.Foreground = MutedBrush;
                        FpsFrameTimeText.Text = "Frame: -- ms";
                        if (fpsSnapshot.Value.RequiresAdmin && !_isAdmin)
                        {
                            AdminWarningButton.Tag = "admin";
                            AdminWarningButton.Content = "🛡️ Clic para activar temp. CPU (Admin)";
                            AdminWarningButton.Visibility = Visibility.Visible;
                        }
                    }
                }
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private static string ResolveCpuTitle(string rawName)
    {
        if (rawName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "CPU (INTEL)";
        if (rawName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            rawName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
            rawName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase) ||
            rawName.Contains("EPYC", StringComparison.OrdinalIgnoreCase))
            return "CPU (AMD)";
        return "CPU";
    }

    private static string CleanHardwareName(string rawName)
    {
        return rawName
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
            .Replace("12-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("16-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("8-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("6-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static SolidColorBrush GetTemperatureBrush(float temp)
    {
        if (temp < 65) return GreenBrush;
        if (temp < 80) return YellowBrush;
        return RedBrush;
    }

    private static SolidColorBrush GetFpsBrush(double fps)
    {
        if (fps >= 60) return GreenBrush;
        if (fps >= 30) return YellowBrush;
        return RedBrush;
    }

    // --- MOSTRAR / OCULTAR FPS ---
    private void ShowFpsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyShowFps(ShowFpsMenuItem.IsChecked, manageMonitor: true);
        SaveSettings();
    }

    private void ApplyShowFps(bool showFps, bool manageMonitor)
    {
        _showFps = showFps;
        if (FpsCard != null)
        {
            FpsCard.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!manageMonitor) return;

        if (showFps)
        {
            _fpsMonitor.Start();
        }
        else
        {
            _fpsMonitor.Stop();
            if (FpsValueText != null)
            {
                FpsValueText.Text = "--";
                FpsValueText.Foreground = MutedBrush;
            }
            if (FpsAppText != null) FpsAppText.Text = "En espera";
            if (FpsFrameTimeText != null) FpsFrameTimeText.Text = "Frame: -- ms";
        }
    }

    // --- MODO MINIMALISTA ---
    private void MinimalistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyMinimalistMode(MinimalistMenuItem.IsChecked);
        SaveSettings();
    }

    private void ApplyMinimalistMode(bool isMinimalist)
    {
        if (CpuTitleText == null || GpuTitleText == null || FpsTitleText == null) return;

        if (isMinimalist)
        {
            CpuTitleText.Text = "CPU";
            GpuTitleText.Text = "GPU";
            FpsTitleText.Text = "FPS";
            CpuTitleText.FontSize = 15;
            GpuTitleText.FontSize = 15;
            FpsTitleText.FontSize = 15;

            CpuNameText.Visibility = Visibility.Collapsed;
            CpuLoadText.Visibility = Visibility.Collapsed;
            GpuNameText.Visibility = Visibility.Collapsed;
            GpuLoadText.Visibility = Visibility.Collapsed;
            FpsAppText.Visibility = Visibility.Collapsed;
            FpsFrameTimeText.Visibility = Visibility.Collapsed;

            CpuCard.Width = double.NaN;
            GpuCard.Width = double.NaN;
            FpsCard.Width = double.NaN;
            CpuCard.MinWidth = 120;
            GpuCard.MinWidth = 120;
            FpsCard.MinWidth = 120;
            CpuCard.Padding = new Thickness(12, 6, 12, 6);
            GpuCard.Padding = new Thickness(12, 6, 12, 6);
            FpsCard.Padding = new Thickness(12, 6, 12, 6);
        }
        else
        {
            CpuTitleText.Text = _cpuTitleLabel;
            GpuTitleText.Text = _gpuTitleLabel;
            FpsTitleText.Text = "FPS (VENTANA)";
            CpuTitleText.FontSize = 11;
            GpuTitleText.FontSize = 11;
            FpsTitleText.FontSize = 11;

            CpuNameText.Visibility = Visibility.Visible;
            CpuLoadText.Visibility = Visibility.Visible;
            GpuNameText.Visibility = Visibility.Visible;
            GpuLoadText.Visibility = Visibility.Visible;
            FpsAppText.Visibility = Visibility.Visible;
            FpsFrameTimeText.Visibility = Visibility.Visible;

            CpuCard.Width = 215;
            GpuCard.Width = 215;
            FpsCard.Width = 215;
            CpuCard.MinWidth = 0;
            GpuCard.MinWidth = 0;
            FpsCard.MinWidth = 0;
            CpuCard.Padding = new Thickness(12, 10, 12, 10);
            GpuCard.Padding = new Thickness(12, 10, 12, 10);
            FpsCard.Padding = new Thickness(12, 10, 12, 10);
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
        if (CardsPanel == null || GpuCard == null || FpsCard == null) return;

        if (isHorizontal)
        {
            CardsPanel.Orientation = Orientation.Horizontal;
            GpuCard.Margin = new Thickness(8, 0, 0, 0);
            FpsCard.Margin = new Thickness(8, 0, 0, 0);
            HorizontalLayoutMenuItem.IsChecked = true;
            VerticalLayoutMenuItem.IsChecked = false;
        }
        else
        {
            CardsPanel.Orientation = Orientation.Vertical;
            GpuCard.Margin = new Thickness(0, 8, 0, 0);
            FpsCard.Margin = new Thickness(0, 8, 0, 0);
            VerticalLayoutMenuItem.IsChecked = true;
            HorizontalLayoutMenuItem.IsChecked = false;
        }
    }

    private async void ActionWarningButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminWarningButton.Tag as string == "pawnio")
        {
            await InstallPawnIoAsync();
            return;
        }

        RestartAsAdmin_Click(sender, e);
    }

    private async Task InstallPawnIoAsync()
    {
        if (_isInstallingPawnIo) return;
        _isInstallingPawnIo = true;
        AdminWarningButton.IsEnabled = false;
        AdminWarningButton.Content = "⏳ Instalando driver PawnIO...";

        bool installed = false;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "winget",
                Arguments = "install --id namazso.PawnIO -e --silent --accept-source-agreements --accept-package-agreements",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(_cts.Token);
                installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
            }
        }
        catch { }

        if (!installed)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://pawnio.eu/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        _isInstallingPawnIo = false;
        AdminWarningButton.IsEnabled = true;
        if (!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled)
        {
            AdminWarningButton.Content = "⬇️ Clic para instalar driver PawnIO (Temp. CPU)";
        }
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();

            string resolvedExe = ElevatedTaskHelper.ResolveExePath(_baseDir);
            if (ElevatedTaskHelper.TryRunElevatedTask(resolvedExe))
            {
                Application.Current.Shutdown();
                return;
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            ProcessStartInfo startInfo = new()
            {
                FileName = exePath,
                WorkingDirectory = _baseDir,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = Path.Combine(AppContext.BaseDirectory, "HardwareWidget.dll");
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
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventDelegate = null;
        _cts.Cancel();
        _fpsMonitor.Dispose();
        try { _computer.Close(); } catch { }
    }
}
