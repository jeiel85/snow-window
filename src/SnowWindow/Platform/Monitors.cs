using SnowWindow.Simulation;

namespace SnowWindow.Platform;

internal static class Monitors
{
    /// <summary>연결된 모니터 목록(물리 픽셀). 순서가 매번 같도록 왼쪽·위쪽 기준으로 정렬한다.</summary>
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        Native.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfoW(hMonitor, ref info))
            {
                return true;
            }

            float scale = 1f;
            if (Native.GetDpiForMonitor(hMonitor, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96f;
            }

            list.Add(new MonitorInfo(ToRect(info.rcMonitor), ToRect(info.rcWork), scale));
            return true;
        };

        Native.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        list.Sort((a, b) => a.Bounds.Left != b.Bounds.Left
            ? a.Bounds.Left.CompareTo(b.Bounds.Left)
            : a.Bounds.Top.CompareTo(b.Bounds.Top));
        return list;
    }

    public static RectI ToRect(Native.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
