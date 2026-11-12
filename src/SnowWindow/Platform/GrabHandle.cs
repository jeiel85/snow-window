using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnowWindow.Platform;

/// <summary>
/// 커서 아래 눈덩이에만 겹쳐 두는 작은 원형 창. 눈 오버레이는 마우스를 모두 통과시키므로,
/// 눈덩이를 잡을 수 있게 그 원 안에서만 클릭을 받는다. 원 밖은 창 영역(SetWindowRgn)이 아니어서
/// 클릭이 아래 창으로 그대로 간다.
/// 눌러도 활성 창이 바뀌지 않고(WS_EX_NOACTIVATE), 누른 채 끄는 동안 마우스를 붙잡아(SetCapture)
/// 아래 창이 끌기 입력을 받지 않는다. 알파 1/255라 눈에 보이지 않는다.
/// 만든 스레드(엔진 스레드)에서만 쓴다. 한 번에 하나만 둔다.
/// </summary>
internal sealed class GrabHandle : IDisposable
{
    private const string ClassName = "SnowWindow.GrabHandle";

    // 네이티브 쪽이 들고 있는 콜백이므로 수집되지 않게 정적으로 붙잡아 둔다.
    private static readonly Native.WndProc Proc = WndProc;
    private static bool s_registered;
    private static GrabHandle? s_instance;

    private readonly nint _hwnd;
    private readonly Func<bool> _onPress;
    private readonly Action _onRelease;
    private int _x;
    private int _y;
    private int _size;
    private bool _shown;

    /// <param name="onPress">왼쪽 버튼을 눌렀을 때. true를 돌려주면 놓을 때까지 마우스를 붙잡는다.</param>
    /// <param name="onRelease">버튼을 놓았거나 붙잡기가 풀렸을 때.</param>
    public GrabHandle(Func<bool> onPress, Action onRelease)
    {
        if (s_instance != null)
        {
            throw new InvalidOperationException("눈덩이 잡기 창은 하나만 만들 수 있습니다.");
        }

        EnsureRegistered();
        const long exStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        _hwnd = Native.CreateWindowExW(
            (uint)exStyle, ClassName, "Snow Window", Native.WS_POPUP,
            0, 0, 1, 1, 0, 0, Native.GetModuleHandleW(null), 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "눈덩이 잡기 창을 만들지 못했습니다.");
        }

        // 알파가 0이면 클릭도 통과하므로 1로 둔다(흰 바탕을 1/255로 겹쳐 그려 눈덩이 위에서 보이지 않는다).
        Native.SetLayeredWindowAttributes(_hwnd, 0, 1, Native.LWA_ALPHA);
        _onPress = onPress;
        _onRelease = onRelease;
        s_instance = this;
    }

    /// <summary>눈덩이를 잡고 있는 중(버튼을 누른 채).</summary>
    public bool Holding { get; private set; }

    /// <summary>(cx, cy) 중심, radius 반지름의 원 위에 둔다.</summary>
    public void ShowOver(float cx, float cy, float radius)
    {
        int size = Math.Max(6, (int)MathF.Ceiling(radius * 2f));
        int x = (int)MathF.Round(cx - size * 0.5f);
        int y = (int)MathF.Round(cy - size * 0.5f);
        if (_shown && x == _x && y == _y && size == _size)
        {
            return;
        }

        if (size != _size)
        {
            nint region = Native.CreateEllipticRgn(0, 0, size, size);
            if (region != 0 && Native.SetWindowRgn(_hwnd, region, true) == 0)
            {
                Native.DeleteObject(region);
            }

            _size = size;
        }

        uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | (_shown ? 0 : Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, size, size, flags);
        _x = x;
        _y = y;
        _shown = true;
    }

    public void Hide()
    {
        if (_shown && !Holding)
        {
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _shown = false;
        }
    }

    /// <summary>잡고 있으면 놓는다(onRelease가 불린다).</summary>
    public void Cancel()
    {
        if (Holding)
        {
            Native.ReleaseCapture();
        }
    }

    public void Dispose()
    {
        Cancel();
        Native.DestroyWindow(_hwnd);
        if (s_instance == this)
        {
            s_instance = null;
        }
    }

    private nint Handle(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                return Native.MA_NOACTIVATE;

            case Native.WM_LBUTTONDOWN:
                if (!Holding && _onPress())
                {
                    Holding = true;
                    Native.SetCapture(_hwnd);
                }

                return 0;

            case Native.WM_LBUTTONUP:
                if (Holding)
                {
                    Native.ReleaseCapture(); // → WM_CAPTURECHANGED
                }

                return 0;

            case Native.WM_CAPTURECHANGED:
                if (Holding)
                {
                    Holding = false;
                    _onRelease();
                }

                return 0;
        }

        return Native.DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

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
            hCursor = Native.LoadCursorW(0, Native.IDC_HAND), // 눈덩이 위에서는 손 모양: 잡을 수 있다는 표시
            hbrBackground = Native.GetStockObject(Native.WHITE_BRUSH),
            lpszClassName = name,
        };
        if (Native.RegisterClassExW(ref wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "눈덩이 잡기 창 클래스를 등록하지 못했습니다.");
        }

        s_registered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_instance;
        return self != null && self._hwnd == hwnd
            ? self.Handle(msg, wParam, lParam)
            : Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
