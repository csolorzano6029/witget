using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SystemPerformanceWidget;

internal readonly record struct FpsSnapshot(
    double? Fps,
    double? FrameTimeMs,
    string AppName,
    int ActivePid,
    uint? UdpRemoteAddr,
    bool RequiresAdmin);

internal sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "SystemPerfWidgetFpsTrace";
    private const double WindowDurationMs = 1500.0;
    private const double StaleTimeoutSeconds = 1.8;

    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9ProviderGuid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlProviderGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly Guid KernelNetworkProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const ulong DxgiEventsKeyword = 0x0000000000000002;
    private const ulong DxgKrnlPresentKeyword = 0x0000000008000000;
    private const ulong KernelNetworkIpv4Keyword = 0x0000000000000010;

    private const int DxgiPresentStartId = 42;
    private const int DxgiMpoPresentStartId = 55;
    private const int D3D9PresentStartId = 1;
    private const int DxgKrnlPresentInfoId = 184;
    private const int KernelNetworkUdpSendId = 42;

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm",
        "csrss",
        "sihost",
        "shellexperiencehost",
        "searchhost",
        "startmenuexperiencehost",
        "textinputhost",
        "lockapp",
        "applicationframehost",
        "system",
        "idle",
        "systemperformancewidget",
        "hardwarewidget",
        "weatherwidget"
    };

    private sealed class PidFrameData
    {
        public readonly List<double> PrimaryTimestampsMs = new(128);
        public readonly List<double> KernelTimestampsMs = new(128);
        public DateTime LastPrimaryUtc;
        public DateTime LastKernelUtc;
        public uint? LastUdpRemoteAddr;
        public DateTime LastUdpUtc;
        public string? ProcessName;
        public bool IsExcluded;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private readonly object _lock = new();
    private readonly Dictionary<int, PidFrameData> _pidFrames = new();
    private readonly int _selfPid = Environment.ProcessId;

    private TraceEventSession? _session;
    private Thread? _workerThread;
    private volatile bool _isRunning;
    private volatile bool _requiresAdmin;
    private int _lastForegroundPid;
    private DateTime _lastCleanupUtc = DateTime.UtcNow;

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _requiresAdmin = false;
        }

        _workerThread = new Thread(RunSessionLoop)
        {
            IsBackground = true,
            Name = "PerfFpsEtwThread"
        };
        _workerThread.Start();
    }

    public void Stop()
    {
        TraceEventSession? sessionToStop;
        Thread? threadToJoin;

        lock (_lock)
        {
            _isRunning = false;
            sessionToStop = _session;
            _session = null;
            threadToJoin = _workerThread;
            _workerThread = null;
            _pidFrames.Clear();
        }

        try { sessionToStop?.Stop(true); } catch { }
        try { sessionToStop?.Dispose(); } catch { }
        try { threadToJoin?.Join(500); } catch { }
    }

    public FpsSnapshot GetCurrentSnapshot()
    {
        if (_requiresAdmin)
            return new FpsSnapshot(null, null, "Requiere Admin", 0, null, true);

        int fgPid = GetEffectiveForegroundPid();
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            CleanupStalePids(nowUtc);

            if (fgPid > 0 && _pidFrames.TryGetValue(fgPid, out PidFrameData? fgData) && !fgData.IsExcluded)
            {
                if (TryComputeMetrics(fgData, nowUtc, out double fps, out double frameTimeMs))
                {
                    string name = fgData.ProcessName != null ? $"{fgData.ProcessName}.exe" : $"PID {fgPid}";
                    uint? udpAddr = (nowUtc - fgData.LastUdpUtc).TotalSeconds <= 15.0 ? fgData.LastUdpRemoteAddr : null;
                    return new FpsSnapshot(fps, frameTimeMs, name, fgPid, udpAddr, false);
                }
            }

            double bestFps = 0;
            double bestFrameTime = 0;
            string? bestName = null;
            int bestPid = 0;
            uint? bestUdpAddr = null;

            foreach ((int pid, PidFrameData data) in _pidFrames)
            {
                if (data.IsExcluded) continue;
                if (TryComputeMetrics(data, nowUtc, out double fps, out double frameTimeMs) && fps >= 15.0 && fps > bestFps)
                {
                    bestFps = fps;
                    bestFrameTime = frameTimeMs;
                    bestName = data.ProcessName != null ? $"{data.ProcessName}.exe" : $"PID {pid}";
                    bestPid = pid;
                    bestUdpAddr = (nowUtc - data.LastUdpUtc).TotalSeconds <= 15.0 ? data.LastUdpRemoteAddr : null;
                }
            }

            if (bestName != null)
                return new FpsSnapshot(bestFps, bestFrameTime, bestName, bestPid, bestUdpAddr, false);
        }

        return new FpsSnapshot(null, null, "En espera", 0, null, false);
    }

    private void RunSessionLoop()
    {
        try
        {
            StopOrphanedSession();

            using var session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true
            };

            lock (_lock)
            {
                if (!_isRunning) return;
                _session = session;
            }

            session.EnableProvider(DxgiProviderGuid, TraceEventLevel.Verbose, DxgiEventsKeyword);
            session.EnableProvider(D3D9ProviderGuid, TraceEventLevel.Verbose, ulong.MaxValue);
            session.EnableProvider(DxgKrnlProviderGuid, TraceEventLevel.Informational, DxgKrnlPresentKeyword);
            session.EnableProvider(KernelNetworkProviderGuid, TraceEventLevel.Informational, KernelNetworkIpv4Keyword);

            session.Source.AllEvents += OnTraceEvent;
            session.Source.Process();
        }
        catch (UnauthorizedAccessException)
        {
            _requiresAdmin = true;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            _requiresAdmin = true;
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fps_error.log"), ex.ToString()); } catch { }
            }
        }
        finally
        {
            lock (_lock)
            {
                _session = null;
                _isRunning = false;
            }
        }
    }

    private static void StopOrphanedSession()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName, StringComparer.OrdinalIgnoreCase))
            {
                using var existing = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
                existing.Stop(true);
            }
        }
        catch { }
    }

    private void OnTraceEvent(TraceEvent data)
    {
        if (!_isRunning) return;

        int pid = data.ProcessID;
        if (pid <= 4 || pid == _selfPid) return;

        int eventId = (int)data.ID;
        Guid provider = data.ProviderGuid;

        if (provider == KernelNetworkProviderGuid && eventId == KernelNetworkUdpSendId)
        {
            if (data.EventDataLength >= 12)
            {
                byte[] payload = data.EventData();
                uint daddr = BitConverter.ToUInt32(payload, 8);
                if (GamePingMonitor.IsPublicIPv4(daddr))
                {
                    lock (_lock)
                    {
                        if (_pidFrames.TryGetValue(pid, out PidFrameData? existing) && !existing.IsExcluded)
                        {
                            existing.LastUdpRemoteAddr = daddr;
                            existing.LastUdpUtc = DateTime.UtcNow;
                        }
                    }
                }
            }
            return;
        }

        bool isPrimary;
        if (provider == DxgiProviderGuid && (eventId == DxgiPresentStartId || eventId == DxgiMpoPresentStartId))
            isPrimary = true;
        else if (provider == D3D9ProviderGuid && eventId == D3D9PresentStartId)
            isPrimary = true;
        else if (provider == DxgKrnlProviderGuid && eventId == DxgKrnlPresentInfoId)
            isPrimary = false;
        else
            return;

        double tsMs = data.TimeStampRelativeMSec;
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            if (!_pidFrames.TryGetValue(pid, out PidFrameData? frameData))
            {
                string? procName = TryGetProcessName(pid);
                bool excluded = procName != null && ExcludedProcesses.Contains(procName);
                frameData = new PidFrameData
                {
                    ProcessName = procName,
                    IsExcluded = excluded
                };
                _pidFrames[pid] = frameData;
            }

            if (frameData.IsExcluded) return;

            if (isPrimary)
            {
                AppendTimestamp(frameData.PrimaryTimestampsMs, tsMs);
                frameData.LastPrimaryUtc = nowUtc;
            }
            else
            {
                AppendTimestamp(frameData.KernelTimestampsMs, tsMs);
                frameData.LastKernelUtc = nowUtc;
            }
        }
    }

    private static void AppendTimestamp(List<double> list, double tsMs)
    {
        if (list.Count > 0 && tsMs < list[^1]) return;

        list.Add(tsMs);
        double cutoff = tsMs - WindowDurationMs;
        int removeCount = 0;
        while (removeCount < list.Count && list[removeCount] < cutoff) removeCount++;
        if (removeCount > 0) list.RemoveRange(0, removeCount);
    }

    private static bool TryComputeMetrics(PidFrameData data, DateTime nowUtc, out double fps, out double frameTimeMs)
    {
        bool hasPrimary = (nowUtc - data.LastPrimaryUtc).TotalSeconds <= StaleTimeoutSeconds &&
                          data.PrimaryTimestampsMs.Count >= 2;

        List<double>? timestamps = hasPrimary
            ? data.PrimaryTimestampsMs
            : ((nowUtc - data.LastKernelUtc).TotalSeconds <= StaleTimeoutSeconds && data.KernelTimestampsMs.Count >= 2
                ? data.KernelTimestampsMs
                : null);

        if (timestamps == null)
        {
            fps = 0;
            frameTimeMs = 0;
            return false;
        }

        double spanMs = timestamps[^1] - timestamps[0];
        if (spanMs < 50.0)
        {
            fps = 0;
            frameTimeMs = 0;
            return false;
        }

        int intervals = timestamps.Count - 1;
        fps = intervals * 1000.0 / spanMs;
        frameTimeMs = spanMs / intervals;
        return fps is > 0.5 and < 5000.0;
    }

    private void CleanupStalePids(DateTime nowUtc)
    {
        if ((nowUtc - _lastCleanupUtc).TotalSeconds < 10.0) return;
        _lastCleanupUtc = nowUtc;

        List<int>? toRemove = null;
        foreach ((int pid, PidFrameData data) in _pidFrames)
        {
            DateTime latest = data.LastPrimaryUtc > data.LastKernelUtc ? data.LastPrimaryUtc : data.LastKernelUtc;
            if ((nowUtc - latest).TotalSeconds > 15.0)
            {
                toRemove ??= new List<int>();
                toRemove.Add(pid);
            }
        }

        if (toRemove != null)
        {
            foreach (int pid in toRemove) _pidFrames.Remove(pid);
        }
    }

    private int GetEffectiveForegroundPid()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return _lastForegroundPid;

        GetWindowThreadProcessId(hwnd, out uint pidU);
        int pid = unchecked((int)pidU);
        if (pid <= 4) return _lastForegroundPid;
        if (pid == _selfPid) return _lastForegroundPid;

        int childPid = FindUwpChildPid(hwnd, pid);
        if (childPid > 4 && childPid != _selfPid) pid = childPid;

        _lastForegroundPid = pid;
        return pid;
    }

    private static int FindUwpChildPid(IntPtr parentHwnd, int parentPid)
    {
        int foundPid = 0;
        EnumChildWindows(parentHwnd, (childHwnd, lParam) =>
        {
            GetWindowThreadProcessId(childHwnd, out uint childPidU);
            int childPid = unchecked((int)childPidU);
            if (childPid > 4 && childPid != parentPid)
            {
                foundPid = childPid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return foundPid;
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using Process proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
