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
using System.Windows.Media.Effects;
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
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowFps { get; set; } = true;
    public bool ShowPing { get; set; } = true;
    public bool TransparentBackground { get; set; } = false;
    public string? BackgroundMode { get; set; }
    public double IndicatorSpacing { get; set; } = 8.0;
    public bool Topmost { get; set; } = true;
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
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int VK_MENU = 0x12;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_ID = 0x5101;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    private static readonly SolidColorBrush GreenBrush = CreateFrozenBrush(0, 230, 118);
    private static readonly SolidColorBrush YellowBrush = CreateFrozenBrush(255, 179, 0);
    private static readonly SolidColorBrush RedBrush = CreateFrozenBrush(255, 82, 82);
    private static readonly SolidColorBrush MutedBrush = CreateFrozenBrush(150, 160, 175);
    private static readonly SolidColorBrush CardBackgroundBrush = CreateFrozenBrush(0xEE, 0x14, 0x17, 0x20);
    private static readonly SolidColorBrush CardBorderBrush = CreateFrozenBrush(0x38, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush InvisibleHitBrush = CreateFrozenBrush(0x01, 0x00, 0x00, 0x00);
    private static readonly DropShadowEffect TextShadowEffect = CreateFrozenShadow();

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly FpsMonitor _fpsMonitor = new();
    private readonly GamePingMonitor _pingMonitor = new();
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
    private string _backgroundMode = "Individual";
    private bool _isInitializing = true;
    private bool _closedBySignal = false;
    private bool _isClickThroughApplied = false;
    private volatile bool _isHiddenByHotkey = false;
    private HwndSource? _hwndSource;
    private int _instanceCheckTick;
    private volatile bool _wasPawnIoInstalled;
    private volatile bool _isInstallingPawnIo;
    private volatile bool _showCpu = true;
    private volatile bool _showGpu = true;
    private volatile bool _showRam = true;
    private volatile bool _showFps = true;
    private volatile bool _showPing = true;
    private volatile bool _isDragging;
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b) =>
        CreateFrozenBrush(255, r, g, b);

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateFrozenShadow()
    {
        var effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 4,
            ShadowDepth = 1,
            Opacity = 0.90
        };
        effect.Freeze();
        return effect;
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

        // Timer ligero: verifica instancia única cada ~480ms, actualiza click-through y asegura orden Z sobre Barra de Tareas
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _topmostTimer.Tick += (_, _) =>
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

                if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && NeedsTopmostRefresh())
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
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ForceAboveTaskbar();
            UpdateClickThroughState();

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(_hwnd, HOTKEY_TOGGLE_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_H);

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
        if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen)
        {
            ForceAboveTaskbar();
        }
    }

    private bool IsCursorOverSettingsButton()
    {
        if (SettingsButton == null || !SettingsButton.IsVisible || SettingsButton.ActualWidth <= 0 || SettingsButton.ActualHeight <= 0)
            return false;

        if (!GetCursorPos(out POINT pt)) return false;

        try
        {
            Point local = SettingsButton.PointFromScreen(new Point(pt.X, pt.Y));
            return local.X >= -2 && local.Y >= -2 &&
                   local.X <= SettingsButton.ActualWidth + 2 &&
                   local.Y <= SettingsButton.ActualHeight + 2;
        }
        catch
        {
            return false;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        WidgetContextMenu.PlacementTarget = SettingsButton;
        WidgetContextMenu.IsOpen = true;
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
        {
            return;
        }

        if (eventType is not (EVENT_SYSTEM_FOREGROUND or EVENT_SYSTEM_MENUPOPUPSTART or EVENT_SYSTEM_CAPTURESTART))
        {
            return;
        }

        if (NeedsTopmostRefresh())
        {
            ForceAboveTaskbar();
        }
        else if (IsTaskbarEventWindow(hwnd))
        {
            // Si la Barra de Tareas recibió clic/foco pero aún está subiendo en el Z-order
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (Topmost && !_isDragging && !WidgetContextMenu.IsOpen && NeedsTopmostRefresh())
                {
                    ForceAboveTaskbar();
                }
            });
        }
    }

    private bool NeedsTopmostRefresh()
    {
        if (_hwnd == IntPtr.Zero || !Topmost) return false;
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOPMOST) == 0) return true;
        return IsTaskbarAboveWidget();
    }

    private bool IsTaskbarAboveWidget()
    {
        if (_hwnd == IntPtr.Zero) return false;

        IntPtr current = GetWindow(_hwnd, GW_HWNDPREV);
        for (int i = 0; i < 256 && current != IntPtr.Zero; i++)
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
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOPMOST) == 0)
            {
                // Si el SO quitó WS_EX_TOPMOST (común al arrancar con Windows), forzar ciclo NOTOPMOST -> TOPMOST
                SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
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
        SpacingSlider.Value = Math.Clamp(_settings.IndicatorSpacing, 0.0, 32.0);
        Topmost = _settings.Topmost;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        LockPositionMenuItem.IsChecked = _settings.LockPosition;
        ClickThroughMenuItem.IsChecked = _settings.ClickThrough;
        MinimalistMenuItem.IsChecked = _settings.MinimalistMode;
        ShowCpuMenuItem.IsChecked = _settings.ShowCpu;
        ShowGpuMenuItem.IsChecked = _settings.ShowGpu;
        ShowRamMenuItem.IsChecked = _settings.ShowRam;
        ShowFpsMenuItem.IsChecked = _settings.ShowFps;
        ShowPingMenuItem.IsChecked = _settings.ShowPing;
        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;

        string initialBgMode = _settings.BackgroundMode switch
        {
            "Unified" => "Unified",
            "None" => "None",
            "Individual" => "Individual",
            _ => _settings.TransparentBackground ? "None" : "Individual"
        };

        ApplyBackgroundMode(initialBgMode);
        ApplyMinimalistMode(_settings.MinimalistMode);
        ApplyShowCpu(_settings.ShowCpu);
        ApplyShowGpu(_settings.ShowGpu);
        ApplyShowRam(_settings.ShowRam);
        ApplyShowFps(_settings.ShowFps, manageMonitor: false);
        ApplyShowPing(_settings.ShowPing, manageMonitor: false);
        ApplyOrientation(_settings.IsHorizontal);
        ApplySpacing(SpacingSlider.Value);
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
            _settings.IndicatorSpacing = SpacingSlider.Value;
            _settings.IsHorizontal = HorizontalLayoutMenuItem.IsChecked;
            _settings.MinimalistMode = MinimalistMenuItem.IsChecked;
            _settings.ShowCpu = ShowCpuMenuItem.IsChecked;
            _settings.ShowGpu = ShowGpuMenuItem.IsChecked;
            _settings.ShowRam = ShowRamMenuItem.IsChecked;
            _settings.ShowFps = ShowFpsMenuItem.IsChecked;
            _settings.ShowPing = ShowPingMenuItem.IsChecked;
            _settings.BackgroundMode = _backgroundMode;
            _settings.TransparentBackground = string.Equals(_backgroundMode, "None", StringComparison.Ordinal);
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

        // Al iniciar sesión en Windows, Explorer y la barra de tareas terminan de cargarse unos segundos después.
        // Reaplicar el Z-order tras esos intervalos garantiza que quede por encima de la barra de tareas.
        _ = Task.Run(async () =>
        {
            foreach (int delayMs in new[] { 600, 2000, 5000, 10000 })
            {
                try
                {
                    await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        if (Topmost && !_isHiddenByHotkey && !_isDragging && !WidgetContextMenu.IsOpen)
                        {
                            ForceAboveTaskbar();
                        }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });

        if (_showFps || _showPing)
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
                        }
                    }
                }
            }

            _cpuTitleLabel = cpuTitle;
            _gpuTitleLabel = gpuTitle;

            FpsSnapshot? fpsSnapshot = (_showFps || _showPing) ? _fpsMonitor.GetCurrentSnapshot() : null;
            GamePingSnapshot? pingSnapshot = (_showPing && fpsSnapshot.HasValue)
                ? _pingMonitor.GetSnapshot(fpsSnapshot.Value.ActivePid, fpsSnapshot.Value.AppName, fpsSnapshot.Value.UdpRemoteAddr)
                : null;
            (double usedGb, double totalGb, double percent)? ramSnapshot = _showRam ? TryReadRamStatus() : null;

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

                if (_showRam)
                {
                    UpdateRamUi(ramSnapshot);
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

                Visibility prevPingVis = PingCard.Visibility;
                if (_showPing && pingSnapshot.HasValue && pingSnapshot.Value.HasActiveGame)
                {
                    PingCard.Visibility = Visibility.Visible;
                    PingGameText.Text = pingSnapshot.Value.GameName;
                    PingServerText.Text = string.IsNullOrEmpty(pingSnapshot.Value.ServerIp)
                        ? "Servidor: --"
                        : $"IP: {pingSnapshot.Value.ServerIp}";
                    if (pingSnapshot.Value.PingMs is int ms)
                    {
                        PingValueText.Text = $"{ms} ms";
                        PingValueText.Foreground = GetPingBrush(ms);
                    }
                    else
                    {
                        PingValueText.Text = "-- ms";
                        PingValueText.Foreground = MutedBrush;
                    }
                }
                else
                {
                    PingCard.Visibility = Visibility.Collapsed;
                }

                if (PingCard.Visibility != prevPingVis)
                {
                    UpdateCardMargins();
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

    private static SolidColorBrush GetRamBrush(double percent)
    {
        if (percent < 70) return GreenBrush;
        if (percent < 85) return YellowBrush;
        return RedBrush;
    }

    private static SolidColorBrush GetPingBrush(int pingMs)
    {
        if (pingMs < 60) return GreenBrush;
        if (pingMs < 120) return YellowBrush;
        return RedBrush;
    }

    private static (double usedGb, double totalGb, double percent)? TryReadRamStatus()
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
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / bytesPerGb;
            double percent = usedGb * 100.0 / totalGb;
            return (usedGb, totalGb, percent);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRamUi((double usedGb, double totalGb, double percent)? snapshot)
    {
        if (RamUsageDetailText == null || RamPercentText == null || RamMainText == null) return;

        if (snapshot is { } ram)
        {
            double usedRounded = Math.Round(ram.usedGb, MidpointRounding.AwayFromZero);
            double totalRounded = Math.Round(ram.totalGb, MidpointRounding.AwayFromZero);

            RamUsageDetailText.Text = $"{usedRounded:0} / {totalRounded:0} GB";
            RamPercentText.Text = $"Uso: {ram.percent:0}%";
            RamMainText.Text = MinimalistMenuItem?.IsChecked == true
                ? $"{usedRounded:0}/{totalRounded:0} GB"
                : $"{ram.percent:0}%";
            RamMainText.Foreground = GetRamBrush(ram.percent);
        }
        else
        {
            RamUsageDetailText.Text = "-- / -- GB";
            RamPercentText.Text = "Uso: --%";
            RamMainText.Text = MinimalistMenuItem?.IsChecked == true ? "--/-- GB" : "--%";
            RamMainText.Foreground = MutedBrush;
        }
    }

    // --- MOSTRAR / OCULTAR CPU ---
    private void ShowCpuMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyShowCpu(ShowCpuMenuItem.IsChecked);
        SaveSettings();
    }

    private void ApplyShowCpu(bool showCpu)
    {
        _showCpu = showCpu;
        if (CpuCard != null)
        {
            CpuCard.Visibility = showCpu ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCardMargins();
    }

    // --- MOSTRAR / OCULTAR GPU ---
    private void ShowGpuMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyShowGpu(ShowGpuMenuItem.IsChecked);
        SaveSettings();
    }

    private void ApplyShowGpu(bool showGpu)
    {
        _showGpu = showGpu;
        if (GpuCard != null)
        {
            GpuCard.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCardMargins();
    }

    // --- MOSTRAR / OCULTAR RAM ---
    private void ShowRamMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyShowRam(ShowRamMenuItem.IsChecked);
        SaveSettings();
    }

    private void ApplyShowRam(bool showRam)
    {
        _showRam = showRam;
        if (RamCard != null)
        {
            RamCard.Visibility = showRam ? Visibility.Visible : Visibility.Collapsed;
        }

        if (showRam)
        {
            UpdateRamUi(TryReadRamStatus());
        }
        UpdateCardMargins();
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
        UpdateCardMargins();

        if (!manageMonitor) return;

        if (showFps)
        {
            _fpsMonitor.Start();
        }
        else
        {
            if (!_showPing)
            {
                _fpsMonitor.Stop();
            }
            if (FpsValueText != null)
            {
                FpsValueText.Text = "--";
                FpsValueText.Foreground = MutedBrush;
            }
            if (FpsAppText != null) FpsAppText.Text = "En espera";
            if (FpsFrameTimeText != null) FpsFrameTimeText.Text = "Frame: -- ms";
        }
    }

    // --- MOSTRAR / OCULTAR PING (JUEGO) ---
    private void ShowPingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyShowPing(ShowPingMenuItem.IsChecked, manageMonitor: true);
        SaveSettings();
    }

    private void ApplyShowPing(bool showPing, bool manageMonitor)
    {
        _showPing = showPing;
        if (!showPing && PingCard != null)
        {
            PingCard.Visibility = Visibility.Collapsed;
        }
        UpdateCardMargins();

        if (!manageMonitor) return;

        if (showPing)
        {
            _fpsMonitor.Start();
        }
        else
        {
            if (!_showFps)
            {
                _fpsMonitor.Stop();
            }
            if (PingValueText != null)
            {
                PingValueText.Text = "-- ms";
                PingValueText.Foreground = MutedBrush;
            }
            if (PingGameText != null) PingGameText.Text = "Sin juego";
            if (PingServerText != null) PingServerText.Text = "Servidor: --";
        }
    }

    // --- ESTILO DE FONDO (INDIVIDUAL / BLOQUE ÚNICO / SIN FONDO) ---
    private void IndividualBg_Click(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundMode("Individual");
        SaveSettings();
    }

    private void UnifiedBg_Click(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundMode("Unified");
        SaveSettings();
    }

    private void NoBg_Click(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundMode("None");
        SaveSettings();
    }

    private void ApplyBackgroundMode(string mode)
    {
        _backgroundMode = mode;

        if (IndividualBgMenuItem != null) IndividualBgMenuItem.IsChecked = mode == "Individual";
        if (UnifiedBgMenuItem != null) UnifiedBgMenuItem.IsChecked = mode == "Unified";
        if (NoBgMenuItem != null) NoBgMenuItem.IsChecked = mode == "None";

        if (UnifiedContainer == null || CpuCard == null || GpuCard == null || RamCard == null || FpsCard == null || PingCard == null || CardsPanel == null)
            return;

        Border[] allCards = new[] { CpuCard, GpuCard, RamCard, FpsCard, PingCard };

        if (mode == "Unified")
        {
            UnifiedContainer.Background = CardBackgroundBrush;
            UnifiedContainer.BorderBrush = CardBorderBrush;
            UnifiedContainer.BorderThickness = new Thickness(1);
            UnifiedContainer.Padding = MinimalistMenuItem?.IsChecked == true
                ? new Thickness(4, 2, 4, 2)
                : new Thickness(4);

            foreach (Border card in allCards)
            {
                card.Background = Brushes.Transparent;
                card.BorderBrush = Brushes.Transparent;
                card.BorderThickness = new Thickness(0);
            }

            CardsPanel.Effect = null;
        }
        else if (mode == "None")
        {
            UnifiedContainer.Background = Brushes.Transparent;
            UnifiedContainer.BorderBrush = Brushes.Transparent;
            UnifiedContainer.BorderThickness = new Thickness(0);
            UnifiedContainer.Padding = new Thickness(0);

            foreach (Border card in allCards)
            {
                card.Background = InvisibleHitBrush;
                card.BorderBrush = Brushes.Transparent;
                card.BorderThickness = new Thickness(0);
            }

            CardsPanel.Effect = TextShadowEffect;
        }
        else
        {
            UnifiedContainer.Background = Brushes.Transparent;
            UnifiedContainer.BorderBrush = Brushes.Transparent;
            UnifiedContainer.BorderThickness = new Thickness(0);
            UnifiedContainer.Padding = new Thickness(0);

            foreach (Border card in allCards)
            {
                card.Background = CardBackgroundBrush;
                card.BorderBrush = CardBorderBrush;
                card.BorderThickness = new Thickness(1);
            }

            CardsPanel.Effect = null;
        }

        UpdateMinimalistCardSizing(MinimalistMenuItem?.IsChecked == true);
    }

    private void UpdateMinimalistCardSizing(bool isMinimalist)
    {
        if (CpuCard == null || GpuCard == null || RamCard == null || FpsCard == null || PingCard == null) return;

        if (isMinimalist)
        {
            bool individual = _backgroundMode == "Individual";
            CpuCard.Width = double.NaN;
            GpuCard.Width = double.NaN;
            RamCard.Width = double.NaN;
            FpsCard.Width = double.NaN;
            PingCard.Width = double.NaN;
            CpuCard.MinWidth = individual ? 120 : 0;
            GpuCard.MinWidth = individual ? 120 : 0;
            RamCard.MinWidth = individual ? 120 : 0;
            FpsCard.MinWidth = individual ? 120 : 0;
            PingCard.MinWidth = individual ? 120 : 0;
            Thickness padding = individual ? new Thickness(12, 6, 12, 6) : new Thickness(8, 6, 8, 6);
            CpuCard.Padding = padding;
            GpuCard.Padding = padding;
            RamCard.Padding = padding;
            FpsCard.Padding = padding;
            PingCard.Padding = padding;
            if (RamMainText != null) RamMainText.FontSize = 24;
        }
        else
        {
            CpuCard.Width = 215;
            GpuCard.Width = 215;
            RamCard.Width = 215;
            FpsCard.Width = 215;
            PingCard.Width = 215;
            CpuCard.MinWidth = 0;
            GpuCard.MinWidth = 0;
            RamCard.MinWidth = 0;
            FpsCard.MinWidth = 0;
            PingCard.MinWidth = 0;
            Thickness padding = new(12, 10, 12, 10);
            CpuCard.Padding = padding;
            GpuCard.Padding = padding;
            RamCard.Padding = padding;
            FpsCard.Padding = padding;
            PingCard.Padding = padding;
            if (RamMainText != null) RamMainText.FontSize = 24;
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
        if (CpuTitleText == null || GpuTitleText == null || RamTitleText == null || FpsTitleText == null || PingTitleText == null) return;

        if (isMinimalist)
        {
            CpuTitleText.Text = "CPU";
            GpuTitleText.Text = "GPU";
            RamTitleText.Text = "RAM";
            FpsTitleText.Text = "FPS";
            PingTitleText.Text = "PING";
            CpuTitleText.FontSize = 24;
            GpuTitleText.FontSize = 24;
            RamTitleText.FontSize = 24;
            FpsTitleText.FontSize = 24;
            PingTitleText.FontSize = 24;

            CpuNameText.Visibility = Visibility.Collapsed;
            CpuLoadText.Visibility = Visibility.Collapsed;
            GpuNameText.Visibility = Visibility.Collapsed;
            GpuLoadText.Visibility = Visibility.Collapsed;
            RamUsageDetailText.Visibility = Visibility.Collapsed;
            RamPercentText.Visibility = Visibility.Collapsed;
            FpsAppText.Visibility = Visibility.Collapsed;
            FpsFrameTimeText.Visibility = Visibility.Collapsed;
            PingGameText.Visibility = Visibility.Collapsed;
            PingServerText.Visibility = Visibility.Collapsed;
        }
        else
        {
            CpuTitleText.Text = _cpuTitleLabel;
            GpuTitleText.Text = _gpuTitleLabel;
            RamTitleText.Text = "RAM";
            FpsTitleText.Text = "FPS (VENTANA)";
            PingTitleText.Text = "PING (JUEGO)";
            CpuTitleText.FontSize = 11;
            GpuTitleText.FontSize = 11;
            RamTitleText.FontSize = 11;
            FpsTitleText.FontSize = 11;
            PingTitleText.FontSize = 11;

            CpuNameText.Visibility = Visibility.Visible;
            CpuLoadText.Visibility = Visibility.Visible;
            GpuNameText.Visibility = Visibility.Visible;
            GpuLoadText.Visibility = Visibility.Visible;
            RamUsageDetailText.Visibility = Visibility.Visible;
            RamPercentText.Visibility = Visibility.Visible;
            FpsAppText.Visibility = Visibility.Visible;
            FpsFrameTimeText.Visibility = Visibility.Visible;
            PingGameText.Visibility = Visibility.Visible;
            PingServerText.Visibility = Visibility.Visible;
        }

        if (UnifiedContainer != null && _backgroundMode == "Unified")
        {
            UnifiedContainer.Padding = isMinimalist ? new Thickness(4, 2, 4, 2) : new Thickness(4);
        }

        UpdateMinimalistCardSizing(isMinimalist);
        if (_showRam)
        {
            UpdateRamUi(TryReadRamStatus());
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

    // --- SEPARACIÓN ENTRE INDICADORES (SLIDER) ---
    private void SpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplySpacing(e.NewValue);
        SaveSettings();
    }

    private void ApplySpacing(double spacing)
    {
        if (SpacingValueText != null)
            SpacingValueText.Text = $"{spacing:0} px";
        UpdateCardMargins();
    }

    private void UpdateCardMargins()
    {
        if (CpuCard == null || GpuCard == null || RamCard == null || FpsCard == null || PingCard == null || SpacingSlider == null) return;
        double spacing = SpacingSlider.Value;
        bool isHorizontal = HorizontalLayoutMenuItem?.IsChecked == true;
        Thickness margin = isHorizontal
            ? new Thickness(spacing, 0, 0, 0)
            : new Thickness(0, spacing, 0, 0);

        bool seenVisible = false;
        foreach (Border card in new[] { CpuCard, GpuCard, RamCard, FpsCard, PingCard })
        {
            if (card.Visibility == Visibility.Visible)
            {
                card.Margin = seenVisible ? margin : new Thickness(0);
                seenVisible = true;
            }
            else
            {
                card.Margin = new Thickness(0);
            }
        }

        if (SettingsButton != null)
        {
            SettingsButton.Margin = isHorizontal
                ? new Thickness(seenVisible ? 4 : 0, 0, 0, 0)
                : new Thickness(0, seenVisible ? 4 : 0, 0, 0);
            SettingsButton.HorizontalAlignment = isHorizontal
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Right;
        }
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
            HorizontalLayoutMenuItem.IsChecked = true;
            VerticalLayoutMenuItem.IsChecked = false;
        }
        else
        {
            CardsPanel.Orientation = Orientation.Vertical;
            VerticalLayoutMenuItem.IsChecked = true;
            HorizontalLayoutMenuItem.IsChecked = false;
        }

        UpdateCardMargins();
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

    private void ClickThroughMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateClickThroughState();
        SaveSettings();
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
        _topmostTimer.Stop();
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
        _pingMonitor.Dispose();
        try { _computer.Close(); } catch { }
    }
}
