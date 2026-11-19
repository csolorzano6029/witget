using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
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

namespace SystemPerformanceWidget;

public class PerfWidgetSettings
{
    public bool MinimalistMode { get; set; } = false;
    public bool MinimalistVertical { get; set; } = false;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowFps { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowActionButtons { get; set; } = false;
    public double Opacity { get; set; } = 0.94;
    public double Scale { get; set; } = 1.0;
    public double CustomWidth { get; set; } = 378.0;
    public bool Topmost { get; set; } = false;
    public bool LockPosition { get; set; } = false;
    public bool ClickThrough { get; set; } = false;
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
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int VK_MENU = 0x12;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_ID = 0x5102;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_R = 0x52;
    private const int MaxHistorySamples = 60;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static readonly Color CpuLineColor = Color.FromRgb(255, 120, 73);
    private static readonly Color GpuLineColor = Color.FromRgb(92, 230, 109);
    private static readonly Color RamLineColor = Color.FromRgb(88, 184, 255);
    private static readonly Color NetLineColor = Color.FromRgb(229, 169, 92);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly FpsMonitor _fpsMonitor = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _instanceTimer;
    private readonly bool _isAdmin;
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _instanceFilePath;
    private readonly string _closeSignalPath;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private readonly List<double> _cpuHistory = new(MaxHistorySamples);
    private readonly List<double> _gpuHistory = new(MaxHistorySamples);
    private readonly List<double> _ramHistory = new(MaxHistorySamples);
    private readonly List<double> _netHistory = new(MaxHistorySamples);
    private readonly List<(DateTime TimeUtc, double Fps)> _fpsHistory = new(MaxHistorySamples);

    private PerfWidgetSettings _settings = new();
    private string _ramTypeLabel = string.Empty;
    private string? _lastFpsApp;
    private long _lastNetRecvBytes;
    private long _lastNetSentBytes;
    private DateTime _lastNetSampleUtc;
    private long _sessionNetRecvBytes;
    private long _sessionNetSentBytes;
    private bool _netInitialized;

    private readonly char[] _classNameBuffer = new char[64];
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate;
    private HwndSource? _hwndSource;
    private bool _isInitializing = true;
    private bool _closedBySignal = false;
    private bool _isClickThroughApplied = false;
    private volatile bool _isHiddenByHotkey = false;
    private int _instanceCheckTick;
    private volatile bool _isDragging;
    private volatile bool _wasPawnIoInstalled;
    private volatile bool _isInstallingPawnIo;
    private volatile bool _showFps = true;

    public MainWindow()
    {
        InitializeComponent();

        _baseDir = ElevatedTaskHelper.ResolveBaseDirectory();
        _settingsPath = Path.Combine(_baseDir, "perf_settings.json");
        _instanceFilePath = Path.Combine(_baseDir, "active_instance.txt");
        _closeSignalPath = Path.Combine(_baseDir, "close.signal");

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

        _instanceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _instanceTimer.Tick += (_, _) =>
        {
            if (++_instanceCheckTick >= 8)
            {
                _instanceCheckTick = 0;
                if (ShouldCloseForNewerInstance())
                {
                    _closedBySignal = true;
                    Close();
                    return;
                }
            }

            if (!_isHiddenByHotkey)
            {
                UpdateClickThroughState();

                if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && IsTaskbarAboveWidget())
                {
                    ForceAboveTaskbar();
                }
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
                    return true;
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
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ForceAboveTaskbar();
            UpdateClickThroughState();

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(_hwnd, HOTKEY_TOGGLE_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_R);

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_TOGGLE_ID)
        {
            ToggleWidgetVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleWidgetVisibility()
    {
        if (_isHiddenByHotkey || Visibility != Visibility.Visible)
        {
            _isHiddenByHotkey = false;
            Show();
            ForceAboveTaskbar();
            UpdateClickThroughState();
        }
        else
        {
            _isHiddenByHotkey = true;
            if (WidgetContextMenu.IsOpen) WidgetContextMenu.IsOpen = false;
            Hide();
        }
    }

    private void ToggleVisibilityMenuItem_Click(object sender, RoutedEventArgs e) => ToggleWidgetVisibility();

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_isHiddenByHotkey) return;

        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            UpdateClickThroughState();
        }

        if (!Topmost || _isDragging || _closedBySignal || WidgetContextMenu.IsOpen || _hwnd == IntPtr.Zero)
            return;

        if (eventType is not (EVENT_SYSTEM_FOREGROUND or EVENT_SYSTEM_MENUPOPUPSTART or EVENT_SYSTEM_CAPTURESTART))
            return;

        if (IsTaskbarAboveWidget())
        {
            ForceAboveTaskbar();
        }
        else if (IsTaskbarEventWindow(hwnd))
        {
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
            if (IsTaskbarWindow(current)) return true;
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

    private void UpdateClickThroughState()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool altPressed = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        bool manualClickThrough = ClickThroughMenuItem?.IsChecked == true;
        bool inFullscreenGame = IsForegroundFullscreenOrBorderless();
        bool hoveringSettingsOnDesktop = !inFullscreenGame && IsCursorOverSettingsButton();

        bool shouldPassThrough = !_isDragging &&
                                 !WidgetContextMenu.IsOpen &&
                                 !altPressed &&
                                 !hoveringSettingsOnDesktop &&
                                 (manualClickThrough || inFullscreenGame);

        if (shouldPassThrough == _isClickThroughApplied) return;

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int newStyle = shouldPassThrough
            ? (exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)
            : ((exStyle & ~WS_EX_TRANSPARENT) | WS_EX_NOACTIVATE);

        SetWindowLong(_hwnd, GWL_EXSTYLE, newStyle);
        _isClickThroughApplied = shouldPassThrough;
    }

    private bool IsCursorOverSettingsButton()
    {
        Button? activeBtn = MinimalistViewCard?.Visibility == Visibility.Visible
            ? MiniSettingsButton
            : SettingsButton;

        if (activeBtn == null || !activeBtn.IsVisible || activeBtn.ActualWidth <= 0 || activeBtn.ActualHeight <= 0)
            return false;

        if (!GetCursorPos(out POINT pt)) return false;

        try
        {
            Point local = activeBtn.PointFromScreen(new Point(pt.X, pt.Y));
            return local.X >= -2 && local.Y >= -2 &&
                   local.X <= activeBtn.ActualWidth + 2 &&
                   local.Y <= activeBtn.ActualHeight + 2;
        }
        catch
        {
            return false;
        }
    }

    private bool IsForegroundFullscreenOrBorderless()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;

        int len = GetClassName(fg, _classNameBuffer, _classNameBuffer.Length);
        if (len > 0)
        {
            ReadOnlySpan<char> cls = _classNameBuffer.AsSpan(0, len);
            if (cls.Equals("Progman", StringComparison.Ordinal) ||
                cls.Equals("WorkerW", StringComparison.Ordinal) ||
                cls.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
                cls.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!GetWindowRect(fg, out RECT winRect)) return false;

        IntPtr monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        return winRect.Left <= mi.rcMonitor.Left + 2 &&
               winRect.Top <= mi.rcMonitor.Top + 2 &&
               winRect.Right >= mi.rcMonitor.Right - 2 &&
               winRect.Bottom >= mi.rcMonitor.Bottom - 2;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<PerfWidgetSettings>(json) ?? new PerfWidgetSettings();
            }
        }
        catch
        {
            _settings = new PerfWidgetSettings();
        }

        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }
        else
        {
            Left = SystemParameters.WorkArea.Width - 420;
            Top = 40;
        }

        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.30, 1.0);
        ScaleSlider.Value = Math.Clamp(_settings.Scale, 0.65, 1.50);
        WidthSlider.Value = Math.Clamp(_settings.CustomWidth, 160.0, 600.0);
        MinimalistMenuItem.IsChecked = _settings.MinimalistMode;
        ShowCpuMenuItem.IsChecked = _settings.ShowCpu;
        ShowGpuMenuItem.IsChecked = _settings.ShowGpu;
        ShowRamMenuItem.IsChecked = _settings.ShowRam;
        ShowFpsMenuItem.IsChecked = _settings.ShowFps;
        ShowNetworkMenuItem.IsChecked = _settings.ShowNetwork;
        ShowActionButtonsMenuItem.IsChecked = _settings.ShowActionButtons;
        Topmost = _settings.Topmost;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        LockPositionMenuItem.IsChecked = _settings.LockPosition;
        ClickThroughMenuItem.IsChecked = _settings.ClickThrough;
        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;

        ApplyVisibleSections(manageFpsMonitor: false);
        ApplyMinimalistOrientation(_settings.MinimalistVertical);
        ApplyMinimalistMode(_settings.MinimalistMode);
        ApplyOpacity(OpacitySlider.Value);
        ApplyScale(ScaleSlider.Value);
        ApplyCustomWidth(WidthSlider.Value);
    }

    private void SaveSettings()
    {
        if (_isInitializing || _closedBySignal) return;
        try
        {
            _settings.MinimalistMode = MinimalistMenuItem.IsChecked;
            _settings.MinimalistVertical = MiniVerticalMenuItem.IsChecked;
            _settings.ShowCpu = ShowCpuMenuItem.IsChecked;
            _settings.ShowGpu = ShowGpuMenuItem.IsChecked;
            _settings.ShowRam = ShowRamMenuItem.IsChecked;
            _settings.ShowFps = ShowFpsMenuItem.IsChecked;
            _settings.ShowNetwork = ShowNetworkMenuItem.IsChecked;
            _settings.ShowActionButtons = ShowActionButtonsMenuItem.IsChecked;
            _settings.Opacity = OpacitySlider.Value;
            _settings.Scale = ScaleSlider.Value;
            _settings.CustomWidth = WidthSlider.Value;
            _settings.Topmost = Topmost;
            _settings.LockPosition = LockPositionMenuItem.IsChecked;
            _settings.ClickThrough = ClickThroughMenuItem.IsChecked;
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

            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (_isAdmin && ElevatedTaskHelper.RegisterOrUpdateTask(exePath, _baseDir, enable))
            {
                runKey?.DeleteValue("SystemPerformanceWidget", throwOnMissingValue: false);
                return;
            }

            if (enable)
                runKey?.SetValue("SystemPerformanceWidget", $"\"{exePath}\"");
            else
                runKey?.DeleteValue("SystemPerformanceWidget", throwOnMissingValue: false);
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
        if (_showFps)
        {
            _fpsMonitor.Start();
        }

        _ = Task.Run(() =>
        {
            _ramTypeLabel = DetectRamDdrType();
        });

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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0));
        try
        {
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

                    ReadMetricsAndDispatch(pawnIoNow);
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private void ReadMetricsAndDispatch(bool pawnIoInstalled)
    {
        try
        {
            _computer.Accept(_updateVisitor);

            float? cpuTemp = null;
            float? cpuLoad = null;
            float? cpuClockMhz = null;
            string cpuName = "Detectando...";

            float? gpuTemp = null;
            float? gpuLoad = null;
            float? gpuClockMhz = null;
            string gpuName = "Detectando...";
            int bestGpuPriority = 0;

            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuName = CleanDisplayName(hardware.Name);
                    int cpuTempPriority = 0;

                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                        {
                            int prio =
                                sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ? 4 :
                                sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ? 3 :
                                sensor.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

                            if (prio > cpuTempPriority)
                            {
                                cpuTempPriority = prio;
                                cpuTemp = sensor.Value.Value;
                            }
                        }
                        else if (sensor.SensorType == SensorType.Load &&
                                 sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) &&
                                 sensor.Value.HasValue)
                        {
                            cpuLoad = sensor.Value.Value;
                        }
                        else if (sensor.SensorType == SensorType.Clock &&
                                 sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                                 sensor.Value.HasValue &&
                                 (!cpuClockMhz.HasValue || sensor.Value.Value > cpuClockMhz.Value))
                        {
                            cpuClockMhz = sensor.Value.Value;
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
                        gpuName = CleanDisplayName(hardware.Name);
                        gpuTemp = null;
                        gpuLoad = null;
                        gpuClockMhz = null;
                        int gpuTempPriority = 0;
                        int gpuLoadPriority = 0;

                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                int tempPriority =
                                    sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ? 4 :
                                    sensor.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase) ? 3 :
                                    !sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

                                if (tempPriority > gpuTempPriority)
                                {
                                    gpuTempPriority = tempPriority;
                                    gpuTemp = sensor.Value.Value;
                                }
                            }
                            else if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
                            {
                                int loadPriority =
                                    sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ? 3 :
                                    sensor.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

                                if (loadPriority > gpuLoadPriority)
                                {
                                    gpuLoadPriority = loadPriority;
                                    gpuLoad = sensor.Value.Value;
                                }
                            }
                            else if (sensor.SensorType == SensorType.Clock &&
                                     sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                                     sensor.Value.HasValue)
                            {
                                gpuClockMhz = sensor.Value.Value;
                            }
                        }
                    }
                }
            }

            var ram = TryReadRamStatus();
            var net = ReadNetworkSpeeds();
            FpsSnapshot fps = _showFps ? _fpsMonitor.GetCurrentSnapshot() : new FpsSnapshot(null, null, "En espera", false);

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isDragging || _closedBySignal) return;

                // CPU
                CpuHeaderText.Text = $"CPU: {cpuName}";
                CpuTempText.Text = cpuTemp.HasValue && cpuTemp.Value > 0 ? $"{cpuTemp.Value:0} °C" : "-- °C";
                CpuLoadText.Text = cpuLoad.HasValue ? $"{cpuLoad.Value:0}%" : "--%";
                MiniCpuTempText.Text = cpuTemp.HasValue && cpuTemp.Value > 0 ? $"{cpuTemp.Value:0}°C" : "--°C";
                MiniCpuLoadText.Text = cpuLoad.HasValue ? $"{cpuLoad.Value:0}%" : "--%";

                AppendHistory(_cpuHistory, cpuLoad ?? 0);
                SparklineHelper.Render(CpuSparklineCanvas, _cpuHistory, 0, 100, CpuLineColor);

                // GPU
                GpuHeaderText.Text = $"GPU: {gpuName}";
                GpuTempText.Text = gpuTemp.HasValue && gpuTemp.Value > 0 ? $"{gpuTemp.Value:0} °C" : "-- °C";
                GpuLoadText.Text = gpuLoad.HasValue ? $"{gpuLoad.Value:0}%" : "--%";
                MiniGpuTempText.Text = gpuTemp.HasValue && gpuTemp.Value > 0 ? $"{gpuTemp.Value:0}°C" : "--°C";
                MiniGpuLoadText.Text = gpuLoad.HasValue ? $"{gpuLoad.Value:0}%" : "--%";

                AppendHistory(_gpuHistory, gpuLoad ?? 0);
                SparklineHelper.Render(GpuSparklineCanvas, _gpuHistory, 0, 100, GpuLineColor);

                // RAM
                if (ram is { } r)
                {
                    double usedRounded = Math.Round(r.usedGb, MidpointRounding.AwayFromZero);
                    double totalRounded = Math.Round(r.totalGb, MidpointRounding.AwayFromZero);
                    string ddrSuffix = string.IsNullOrEmpty(_ramTypeLabel) ? "" : $" {_ramTypeLabel}";
                    RamHeaderText.Text = $"RAM: {totalRounded:0} GB{ddrSuffix}";
                    RamSubText.Text = $"{r.usedGb:0.0} GB Usados ({r.percent:0}%)";
                    MiniRamText.Text = $"{usedRounded:0}/{totalRounded:0}G";
                    ExtraRamText.Text = $"RAM disponible: {r.availGb:0.1} GB / {totalRounded:0} GB";

                    AppendHistory(_ramHistory, r.percent);
                    SparklineHelper.Render(RamSparklineCanvas, _ramHistory, 0, 100, RamLineColor);

                    if (RamProgressFill.Parent is FrameworkElement track && track.ActualWidth > 0)
                    {
                        RamProgressFill.Width = track.ActualWidth * Math.Clamp(r.percent / 100.0, 0.0, 1.0);
                    }
                }

                // FPS + Min/Max últimos 60s
                DateTime nowUtc = DateTime.UtcNow;
                if (fps.Fps is double currentFps)
                {
                    if (!string.Equals(_lastFpsApp, fps.AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        _fpsHistory.Clear();
                        _lastFpsApp = fps.AppName;
                    }

                    _fpsHistory.Add((nowUtc, currentFps));
                    _fpsHistory.RemoveAll(x => (nowUtc - x.TimeUtc).TotalSeconds > 60);

                    double minFps = _fpsHistory.Min(x => x.Fps);
                    double maxFps = _fpsHistory.Max(x => x.Fps);

                    FpsValueText.Text = $"{currentFps:0} FPS";
                    MiniFpsText.Text = $"{currentFps:0}";
                    FpsAppText.Text = fps.AppName;
                    FpsMinMaxText.Text = $"Min: {minFps:0} / Max: {maxFps:0}";
                }
                else
                {
                    _fpsHistory.RemoveAll(x => (nowUtc - x.TimeUtc).TotalSeconds > 60);
                    FpsValueText.Text = "-- FPS";
                    MiniFpsText.Text = "--";
                    FpsAppText.Text = fps.AppName;
                    FpsMinMaxText.Text = _fpsHistory.Count > 0
                        ? $"Min: {_fpsHistory.Min(x => x.Fps):0} / Max: {_fpsHistory.Max(x => x.Fps):0}"
                        : "Min: -- / Max: --";
                }

                // Red/Internet
                NetDownText.Text = $"Descarga: {FormatSpeed(net.downBps)}";
                NetUpText.Text = $"Subida: {FormatSpeed(net.upBps)}";
                MiniNetText.Text = FormatSpeed(net.downBps);
                double totalKbps = (net.downBps + net.upBps) / 1024.0;
                AppendHistory(_netHistory, totalKbps);
                SparklineHelper.Render(NetSparklineCanvas, _netHistory, 0, 128, NetLineColor, autoScale: true);

                // Ver más detalles
                string cpuClk = cpuClockMhz.HasValue ? $"CPU {cpuClockMhz.Value / 1000.0:0.00} GHz" : "CPU --";
                string gpuClk = gpuClockMhz.HasValue ? $"GPU {gpuClockMhz.Value:0} MHz" : "GPU --";
                ExtraClockText.Text = $"{cpuClk}  |  {gpuClk}";
                ExtraNetTotalText.Text = $"Sesión red: ↓ {FormatBytes(_sessionNetRecvBytes)}  ↑ {FormatBytes(_sessionNetSentBytes)}";

                // Aviso de Admin / PawnIO solo si falta
                if (cpuTemp.HasValue && cpuTemp.Value > 0 && !fps.RequiresAdmin)
                {
                    ActionWarningButton.Visibility = Visibility.Collapsed;
                }
                else if (!_isAdmin)
                {
                    ActionWarningButton.Tag = "admin";
                    ActionWarningButton.Content = "🛡️ Clic para activar temp. CPU y FPS (Admin)";
                    ActionWarningButton.Visibility = Visibility.Visible;
                }
                else if (!pawnIoInstalled && !cpuTemp.HasValue)
                {
                    ActionWarningButton.Tag = "pawnio";
                    if (!_isInstallingPawnIo)
                        ActionWarningButton.Content = "⬇️ Clic para instalar driver PawnIO (Temp. CPU)";
                    ActionWarningButton.Visibility = Visibility.Visible;
                }
                else
                {
                    ActionWarningButton.Visibility = Visibility.Collapsed;
                }
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private (double downBps, double upBps) ReadNetworkSpeeds()
    {
        try
        {
            long totalRecv = 0;
            long totalSent = 0;

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                IPInterfaceStatistics stats = ni.GetIPStatistics();
                totalRecv += stats.BytesReceived;
                totalSent += stats.BytesSent;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (!_netInitialized)
            {
                _lastNetRecvBytes = totalRecv;
                _lastNetSentBytes = totalSent;
                _lastNetSampleUtc = nowUtc;
                _netInitialized = true;
                return (0, 0);
            }

            double elapsed = Math.Max(0.1, (nowUtc - _lastNetSampleUtc).TotalSeconds);
            long deltaRecv = Math.Max(0, totalRecv - _lastNetRecvBytes);
            long deltaSent = Math.Max(0, totalSent - _lastNetSentBytes);

            _lastNetRecvBytes = totalRecv;
            _lastNetSentBytes = totalSent;
            _lastNetSampleUtc = nowUtc;

            _sessionNetRecvBytes += deltaRecv;
            _sessionNetSentBytes += deltaSent;

            return (deltaRecv / elapsed, deltaSent / elapsed);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (double usedGb, double totalGb, double availGb, double percent)? TryReadRamStatus()
    {
        try
        {
            MEMORYSTATUSEX mem = new()
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
            if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
                return null;

            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            double totalGb = mem.ullTotalPhys / bytesPerGb;
            double availGb = mem.ullAvailPhys / bytesPerGb;
            double usedGb = totalGb - availGb;
            double percent = usedGb * 100.0 / totalGb;
            return (usedGb, totalGb, availGb, percent);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectRamDdrType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
            {
                int smbios = obj["SMBIOSMemoryType"] != null ? Convert.ToInt32(obj["SMBIOSMemoryType"]) : 0;
                int memType = obj["MemoryType"] != null ? Convert.ToInt32(obj["MemoryType"]) : 0;
                int code = smbios != 0 ? smbios : memType;
                string label = code switch
                {
                    20 => "DDR",
                    21 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(label)) return label;
            }
        }
        catch { }
        return string.Empty;
    }

    private static void AppendHistory(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > MaxHistorySamples)
            list.RemoveAt(0);
    }

    private static string CleanDisplayName(string rawName)
    {
        return rawName
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
            .Replace("12-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("16-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("8-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("6-Core", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        double mb = bytesPerSec / (1024.0 * 1024.0);
        if (mb >= 1.0) return $"{mb:0.0} MB/s";
        double kb = bytesPerSec / 1024.0;
        return $"{kb:0.0} KB/s";
    }

    private static string FormatBytes(long bytes)
    {
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gb >= 1.0) return $"{gb:0.00} GB";
        double mb = bytes / (1024.0 * 1024.0);
        return $"{mb:0.0} MB";
    }

    private void VisibleSectionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyVisibleSections(manageFpsMonitor: true);
        SaveSettings();
    }

    private void ApplyVisibleSections(bool manageFpsMonitor)
    {
        bool showCpu = ShowCpuMenuItem?.IsChecked ?? true;
        bool showGpu = ShowGpuMenuItem?.IsChecked ?? true;
        bool showRam = ShowRamMenuItem?.IsChecked ?? true;
        bool showFps = ShowFpsMenuItem?.IsChecked ?? true;
        bool showNet = ShowNetworkMenuItem?.IsChecked ?? true;
        bool showButtons = ShowActionButtonsMenuItem?.IsChecked ?? false;

        _showFps = showFps;

        if (CpuFullCard != null) CpuFullCard.Visibility = showCpu ? Visibility.Visible : Visibility.Collapsed;
        if (GpuFullCard != null) GpuFullCard.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        if (RamFullCard != null) RamFullCard.Visibility = showRam ? Visibility.Visible : Visibility.Collapsed;
        if (FpsFullSection != null)
        {
            FpsFullSection.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
            FpsFullSection.Margin = (showNet || showButtons) ? new Thickness(0, 0, 0, 12) : new Thickness(0);
        }
        if (NetFullSection != null)
        {
            NetFullSection.Visibility = showNet ? Visibility.Visible : Visibility.Collapsed;
            NetFullSection.Margin = showButtons ? new Thickness(0, 0, 0, 12) : new Thickness(0);
        }
        if (ActionButtonsGrid != null)
        {
            ActionButtonsGrid.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!showButtons && DetailsPanel != null)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            if (MoreDetailsButton != null) MoreDetailsButton.Content = "Ver más";
        }
        if (BottomFullCard != null)
        {
            BottomFullCard.Visibility = (showFps || showNet || showButtons) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (MiniCpuPill != null) MiniCpuPill.Visibility = showCpu ? Visibility.Visible : Visibility.Collapsed;
        if (MiniGpuPill != null) MiniGpuPill.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        if (MiniRamPill != null) MiniRamPill.Visibility = showRam ? Visibility.Visible : Visibility.Collapsed;
        if (MiniFpsPill != null) MiniFpsPill.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        if (MiniNetPill != null) MiniNetPill.Visibility = showNet ? Visibility.Visible : Visibility.Collapsed;

        if (manageFpsMonitor)
        {
            if (showFps)
            {
                _fpsMonitor.Start();
            }
            else
            {
                _fpsMonitor.Stop();
                _fpsHistory.Clear();
                if (FpsValueText != null) FpsValueText.Text = "-- FPS";
                if (MiniFpsText != null) MiniFpsText.Text = "--";
                if (FpsAppText != null) FpsAppText.Text = "En espera";
                if (FpsMinMaxText != null) FpsMinMaxText.Text = "Min: -- / Max: --";
            }
        }
    }

    private void MinimalistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyMinimalistMode(MinimalistMenuItem.IsChecked);
        SaveSettings();
    }

    private void MiniHorizontal_Click(object sender, RoutedEventArgs e)
    {
        ApplyMinimalistOrientation(isVertical: false);
        if (!MinimalistMenuItem.IsChecked)
        {
            MinimalistMenuItem.IsChecked = true;
            ApplyMinimalistMode(true);
        }
        SaveSettings();
    }

    private void MiniVertical_Click(object sender, RoutedEventArgs e)
    {
        ApplyMinimalistOrientation(isVertical: true);
        if (!MinimalistMenuItem.IsChecked)
        {
            MinimalistMenuItem.IsChecked = true;
            ApplyMinimalistMode(true);
        }
        SaveSettings();
    }

    private void ApplyMinimalistOrientation(bool isVertical)
    {
        if (MiniHorizontalMenuItem != null) MiniHorizontalMenuItem.IsChecked = !isVertical;
        if (MiniVerticalMenuItem != null) MiniVerticalMenuItem.IsChecked = isVertical;

        if (MinimalistItemsPanel == null || MinimalistViewCard == null ||
            MiniCpuPill == null || MiniGpuPill == null || MiniRamPill == null ||
            MiniFpsPill == null || MiniNetPill == null || MiniSettingsButton == null)
        {
            return;
        }

        MinimalistItemsPanel.Orientation = isVertical ? Orientation.Vertical : Orientation.Horizontal;

        if (isVertical)
        {
            MinimalistViewCard.Padding = new Thickness(8, 8, 8, 6);
            Thickness pillMargin = new(0, 0, 0, 6);
            MiniCpuPill.Margin = pillMargin;
            MiniGpuPill.Margin = pillMargin;
            MiniRamPill.Margin = pillMargin;
            MiniFpsPill.Margin = pillMargin;
            MiniNetPill.Margin = new Thickness(0, 0, 0, 4);
            MiniSettingsButton.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            MinimalistViewCard.Padding = new Thickness(10, 7, 8, 7);
            Thickness pillMargin = new(0, 0, 6, 0);
            MiniCpuPill.Margin = pillMargin;
            MiniGpuPill.Margin = pillMargin;
            MiniRamPill.Margin = pillMargin;
            MiniFpsPill.Margin = pillMargin;
            MiniNetPill.Margin = new Thickness(0, 0, 4, 0);
            MiniSettingsButton.HorizontalAlignment = HorizontalAlignment.Center;
        }

        if (WidthSlider != null)
        {
            ApplyCustomWidth(WidthSlider.Value);
        }
    }

    private void ApplyMinimalistMode(bool isMinimalist)
    {
        if (FullViewCard == null || MinimalistViewCard == null) return;
        FullViewCard.Visibility = isMinimalist ? Visibility.Collapsed : Visibility.Visible;
        MinimalistViewCard.Visibility = isMinimalist ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        WidgetContextMenu.PlacementTarget = sender as UIElement ?? SettingsButton;
        WidgetContextMenu.IsOpen = true;
    }

    private void MoreDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        bool show = DetailsPanel.Visibility != Visibility.Visible;
        DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MoreDetailsButton.Content = show ? "Ver menos" : "Ver más";
    }

    private void OpenTaskManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskmgr.exe",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void ActionWarningButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActionWarningButton.Tag as string == "pawnio")
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
        ActionWarningButton.IsEnabled = false;
        ActionWarningButton.Content = "⏳ Instalando driver PawnIO...";

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
                Process.Start(new ProcessStartInfo { FileName = "https://pawnio.eu/", UseShellExecute = true });
            }
            catch { }
        }

        _isInstallingPawnIo = false;
        ActionWarningButton.IsEnabled = true;
        if (!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled)
            ActionWarningButton.Content = "⬇️ Clic para instalar driver PawnIO (Temp. CPU)";
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
                string dllPath = Path.Combine(AppContext.BaseDirectory, "SystemPerformanceWidget.dll");
                startInfo.Arguments = $"\"{dllPath}\"";
            }

            Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch { }
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

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyCustomWidth(e.NewValue);
        SaveSettings();
    }

    private void ApplyCustomWidth(double width)
    {
        if (WidthValueText != null)
            WidthValueText.Text = $"{width:0} px";

        if (FullViewCard != null)
        {
            FullViewCard.Width = Math.Max(280.0, width);
        }

        if (MinimalistViewCard != null)
        {
            bool isVertical = MiniVerticalMenuItem?.IsChecked == true;
            if (isVertical)
            {
                MinimalistViewCard.MinWidth = 0;
                MinimalistViewCard.Width = width;
            }
            else
            {
                MinimalistViewCard.Width = double.NaN;
                MinimalistViewCard.MinWidth = width;
            }
        }
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        ForceAboveTaskbar();
        SaveSettings();
    }

    private void LockPositionMenuItem_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void ClickThroughMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateClickThroughState();
        SaveSettings();
    }

    private void StartWithWindowsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAutoStart(StartWithWindowsMenuItem.IsChecked);
        SaveSettings();
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
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_TOGGLE_ID);
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
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
