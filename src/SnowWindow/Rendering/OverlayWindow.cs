using System.ComponentModel;
using System.Runtime.InteropServices;
using SnowWindow.Platform;
using SnowWindow.Simulation;

namespace SnowWindow.Rendering;

/// <summary>
/// 모니터 하나를 덮는 투명·클릭 통과·항상 위 창. 내용은 DirectComposition으로만 그린다
/// (WS_EX_NOREDIRECTIONBITMAP: GDI 리다이렉션 비트맵이 없어 화면 전체 크기여도 메모리·복사 비용이 들지 않는다).
/// </summary>
internal static class OverlayWindow
{
    private const string ClassName = "SnowWindow.Overlay";

    // 네이티브 쪽이 들고 있는 콜백이므로 수집되지 않게 정적으로 붙잡아 둔다.
    private static readonly Native.WndProc Proc = WndProc;
    private static bool s_registered;

    public static nint Create(RectI bounds)
    {
        EnsureRegistered();
        const long exStyle = Native.WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
            | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        nint hwnd = Native.CreateWindowExW(
            (uint)exStyle, ClassName, "Snow Window", Native.WS_POPUP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            0, 0, Native.GetModuleHandleW(null), 0);
        if (hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "오버레이 창을 만들지 못했습니다.");
        }

        // 레이어드 창은 알파를 한 번 지정해야 화면에 나타난다. 픽셀 투명도는 DirectComposition이 처리한다.
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
        return hwnd;
    }

    public static void Show(nint hwnd) => Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);

    public static void Hide(nint hwnd) => Native.ShowWindow(hwnd, Native.SW_HIDE);

    public static void BringToTop(nint hwnd) =>
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

    public static void Destroy(nint hwnd) => Native.DestroyWindow(hwnd);

    private static void EnsureRegistered()
    {
        if (s_registered)
        {
            return;
        }

        nint name = Marshal.StringToHGlobalUni(ClassName); // 클래스가 살아 있는 동안 유지(프로세스 수명)
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = name,
        };
        if (Native.RegisterClassExW(ref wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "오버레이 창 클래스를 등록하지 못했습니다.");
        }

        s_registered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam) =>
        msg == Native.WM_NCHITTEST ? Native.HTTRANSPARENT : Native.DefWindowProcW(hwnd, msg, wParam, lParam);
}
