using System.Text;
using SnowWindow.Simulation;

namespace SnowWindow.Platform;

/// <summary>
/// 화면에 보이는 최상위 창을 z-order(위→아래)로 모은다. 매 프레임 호출되므로 할당을 줄였다.
/// </summary>
internal sealed unsafe class WindowTracker
{
    private const int MinHolderWidth = 120;
    private const int MinHolderHeight = 60;

    // 눈을 받지 않는 셸·임시 UI 창. 가림 판정에는 계속 쓰인다.
    private static readonly string[] NonHolderClasses =
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "#32768", // 팝업 메뉴
        "tooltips_class32",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "Shell_InputSwitchTopLevelWindow",
        "SysShadow",
    };

    // 바탕화면 자체. 가리지도, 눈을 받지도 않는다.
    private static readonly string[] DesktopClasses = { "Progman", "WorkerW" };

    private readonly List<WindowSnapshot> _windows = new(128);
    private readonly char[] _classBuffer = new char[256];
    private readonly Native.EnumWindowsProc _callback;
    private readonly uint _ownProcessId;

    public WindowTracker()
    {
        _callback = OnWindow;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    public IReadOnlyList<WindowSnapshot> Scan()
    {
        _windows.Clear();
        Native.EnumWindows(_callback, 0);
        return _windows;
    }

    /// <summary>
    /// 전경 창이 모니터 전체를 덮는(전체 화면 동영상·게임 등) 경우 그 모니터 인덱스를 돌려준다. 없으면 -1.
    /// </summary>
    public int FindFullscreenMonitor(IReadOnlyList<MonitorInfo> monitors)
    {
        nint fg = Native.GetForegroundWindow();
        if (fg == 0 || IsDesktop(fg) || !Native.TryGetFrameBounds(fg, out var r))
        {
            return -1;
        }

        // 최대화된 일반 창(제목 표시줄 있음)은 작업 표시줄이 자동 숨김이어도 전체 화면이 아니다.
        long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE);
        if (Native.IsZoomed(fg) && (style & Native.WS_CAPTION) == Native.WS_CAPTION)
        {
            return -1;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i].Bounds;
            if (r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>진단용: 현재 추적 중인 창 목록을 사람이 읽을 수 있게 만든다.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var w in Scan())
        {
            sb.Append(w.HoldsSnow ? "[snow] " : "[    ] ")
              .Append($"0x{w.Handle:X8} {ClassOf(w.Handle),-34} ")
              .Append($"({w.Bounds.Left},{w.Bounds.Top})-({w.Bounds.Right},{w.Bounds.Bottom}) ")
              .Append(TitleOf(w.Handle))
              .AppendLine();
        }

        return sb.ToString();
    }

    private bool OnWindow(nint hwnd, nint lParam)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
        {
            return true;
        }

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownProcessId)
        {
            return true;
        }

        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TRANSPARENT) != 0)
        {
            return true; // 마우스가 통과하는 다른 오버레이는 무시
        }

        if ((ex & Native.WS_EX_LAYERED) != 0
            && Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags)
            && (flags & Native.LWA_ALPHA) != 0 && alpha == 0)
        {
            return true; // 완전히 투명한 창
        }

        if (Native.IsCloaked(hwnd) || IsDesktop(hwnd))
        {
            return true;
        }

        if (!Native.TryGetFrameBounds(hwnd, out var r))
        {
            return true;
        }

        var rect = Monitors.ToRect(r);
        if (rect.IsEmpty)
        {
            return true;
        }

        _windows.Add(new WindowSnapshot(hwnd, rect, IsSnowHolder(hwnd, ex, rect)));
        return true;
    }

    private bool IsSnowHolder(nint hwnd, long ex, RectI rect)
    {
        if (rect.Width < MinHolderWidth || rect.Height < MinHolderHeight)
        {
            return false;
        }

        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0)
        {
            return false; // 떠 있는 도구 창·알림 등
        }

        var cls = ReadClass(hwnd);
        foreach (var name in NonHolderClasses)
        {
            if (cls.SequenceEqual(name))
            {
                return false;
            }
        }

        // 다른 창에 딸린 제목 표시줄 없는 팝업(드롭다운·자동완성 목록 등)은 금방 사라지므로 제외
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE);
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != 0 && (style & Native.WS_CAPTION) != Native.WS_CAPTION)
        {
            return false;
        }

        return true;
    }

    private bool IsDesktop(nint hwnd)
    {
        var cls = ReadClass(hwnd);
        foreach (var name in DesktopClasses)
        {
            if (cls.SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }

    private ReadOnlySpan<char> ReadClass(nint hwnd)
    {
        fixed (char* p = _classBuffer)
        {
            int n = Native.GetClassNameW(hwnd, p, _classBuffer.Length);
            return _classBuffer.AsSpan(0, Math.Max(0, n));
        }
    }

    private string ClassOf(nint hwnd) => new(ReadClass(hwnd));

    private static string TitleOf(nint hwnd)
    {
        char* buf = stackalloc char[128];
        int n = Native.GetWindowTextW(hwnd, buf, 128);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }
}
