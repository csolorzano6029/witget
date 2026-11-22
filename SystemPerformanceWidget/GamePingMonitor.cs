using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SystemPerformanceWidget;

internal readonly record struct GamePingSnapshot(int? PingMs, string GameName, string? ServerIp, bool HasActiveGame);

internal sealed class GamePingMonitor : IDisposable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint MibTcpStateEstab = 5;

    private static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "sihost", "explorer", "taskmgr", "shellexperiencehost", "searchhost",
        "startmenuexperiencehost", "textinputhost", "lockapp", "applicationframehost", "system", "idle",
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi",
        "discord", "slack", "teams", "ms-teams", "whatsapp", "whatsapp.root", "telegram", "spotify",
        "code", "devenv", "rider64", "idea64", "antigravity ide", "claude", "cursor", "windsurf",
        "notepad", "notepad++", "powershell", "pwsh", "cmd", "windowsterminal", "conhost",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "eosoverlayrenderer-win64-shipping",
        "obs64", "vlc", "mpv", "nvidia overlay", "nvcontainer", "armourycrate", "fancontrol",
        "hardwarewidget", "systemperformancewidget", "weatherwidget", "en espera", "requiere admin"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    private int _activeGamePid;
    private string _activeGameName = "Sin juego";
    private string? _lastServerIp;
    private int? _lastPingMs;
    private DateTime _lastProbeStartUtc = DateTime.MinValue;
    private DateTime _lastSuccessUtc = DateTime.MinValue;
    private bool _probeInFlight;

    public static bool IsLikelyGameProcess(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        string clean = appName.Trim();
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^4];
        if (clean.StartsWith("PID ", StringComparison.OrdinalIgnoreCase))
            return false;
        return !NonGameProcesses.Contains(clean);
    }

    public static bool IsPublicIPv4(uint rawAddr)
    {
        if (rawAddr == 0 || rawAddr == 0xFFFFFFFF) return false;

        byte b0 = (byte)(rawAddr & 0xFF);
        byte b1 = (byte)((rawAddr >> 8) & 0xFF);

        if (b0 == 0 || b0 == 10 || b0 == 127) return false;
        if (b0 == 169 && b1 == 254) return false;
        if (b0 == 172 && b1 >= 16 && b1 <= 31) return false;
        if (b0 == 192 && b1 == 168) return false;
        if (b0 == 100 && b1 >= 64 && b1 <= 127) return false;
        if (b0 >= 224) return false;
        return true;
    }

    public GamePingSnapshot GetSnapshot(int activePid, string activeAppName, uint? udpRemoteAddr)
    {
        if (activePid <= 4 || !IsLikelyGameProcess(activeAppName))
        {
            lock (_lock)
            {
                _activeGamePid = 0;
                _activeGameName = "Sin juego";
                _lastPingMs = null;
                _lastServerIp = null;
            }
            return new GamePingSnapshot(null, "Sin juego", null, false);
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool shouldProbe = false;
        int? pingResult;
        string? serverIp;

        lock (_lock)
        {
            if (_activeGamePid != activePid)
            {
                _activeGamePid = activePid;
                _activeGameName = activeAppName;
                _lastPingMs = null;
                _lastServerIp = null;
                _lastProbeStartUtc = DateTime.MinValue;
            }
            else
            {
                _activeGameName = activeAppName;
            }

            if (_lastPingMs.HasValue && (nowUtc - _lastSuccessUtc).TotalSeconds > 12.0)
            {
                _lastPingMs = null;
            }

            if (!_probeInFlight && (nowUtc - _lastProbeStartUtc).TotalSeconds >= 2.5)
            {
                _probeInFlight = true;
                _lastProbeStartUtc = nowUtc;
                shouldProbe = true;
            }

            pingResult = _lastPingMs;
            serverIp = _lastServerIp;
        }

        if (shouldProbe)
        {
            _ = Task.Run(() => ProbeGameServerAsync(activePid, udpRemoteAddr, _cts.Token));
        }

        return new GamePingSnapshot(pingResult, activeAppName, serverIp, true);
    }

    private async Task ProbeGameServerAsync(int pid, uint? udpRemoteAddr, CancellationToken token)
    {
        try
        {
            IPAddress? udpIp = (udpRemoteAddr.HasValue && IsPublicIPv4(udpRemoteAddr.Value))
                ? new IPAddress(udpRemoteAddr.Value)
                : null;

            (IPAddress? tcpIp, int tcpPort) = FindFirstPublicTcpEndpoint(pid);

            IPAddress? targetIp = udpIp ?? tcpIp;
            if (targetIp == null)
                return;

            int? rtt = await TryIcmpPingAsync(targetIp, token).ConfigureAwait(false);

            if (!rtt.HasValue && tcpIp != null && tcpPort > 0)
            {
                rtt = await TryTcpRttAsync(tcpIp, tcpPort, token).ConfigureAwait(false);
                if (rtt.HasValue) targetIp = tcpIp;
            }

            lock (_lock)
            {
                if (_activeGamePid == pid)
                {
                    _lastServerIp = targetIp.ToString();
                    if (rtt.HasValue)
                    {
                        _lastPingMs = rtt.Value;
                        _lastSuccessUtc = DateTime.UtcNow;
                    }
                }
            }
        }
        catch { }
        finally
        {
            lock (_lock)
            {
                _probeInFlight = false;
            }
        }
    }

    private static async Task<int?> TryIcmpPingAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(address, 1100).WaitAsync(token).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success && reply.RoundtripTime is >= 0 and < 5000)
            {
                return (int)reply.RoundtripTime;
            }
        }
        catch { }
        return null;
    }

    private static async Task<int?> TryTcpRttAsync(IPAddress address, int port, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(1100);

            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)Math.Clamp(sw.ElapsedMilliseconds, 1, 5000);
        }
        catch { }
        return null;
    }

    private static (IPAddress? ip, int port) FindFirstPublicTcpEndpoint(int pid)
    {
        int bufferSize = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AfInet, TcpTableOwnerPidConnections, 0);
        if (bufferSize <= 0) return (null, 0);

        IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint ret = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, AfInet, TcpTableOwnerPidConnections, 0);
            if (ret != 0) return (null, 0);

            int rowCount = Marshal.ReadInt32(tcpTablePtr);
            IntPtr rowPtr = IntPtr.Add(tcpTablePtr, sizeof(int));
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                MIB_TCPROW_OWNER_PID row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.dwOwningPid == (uint)pid &&
                    row.dwState == MibTcpStateEstab &&
                    IsPublicIPv4(row.dwRemoteAddr))
                {
                    int port = ((int)(row.dwRemotePort & 0xFF) << 8) | (int)((row.dwRemotePort >> 8) & 0xFF);
                    return (new IPAddress(row.dwRemoteAddr), port);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(tcpTablePtr);
        }

        return (null, 0);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
