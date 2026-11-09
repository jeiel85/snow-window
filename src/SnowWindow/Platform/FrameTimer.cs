using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnowWindow.Platform;

/// <summary>고해상도 대기 타이머로 일정한 프레임 간격을 맞춘다(Thread.Sleep은 15.6ms 단위로 뭉개진다).</summary>
internal sealed class FrameTimer : IDisposable
{
    private readonly nint _handle;

    public FrameTimer()
    {
        _handle = Native.CreateWaitableTimerExW(0, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "고해상도 타이머를 만들지 못했습니다.");
        }
    }

    public void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        long due = -duration.Ticks; // 음수 = 상대 시간(100ns 단위)
        if (!Native.SetWaitableTimer(_handle, ref due, 0, 0, 0, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "타이머 설정에 실패했습니다.");
        }

        Native.WaitForSingleObject(_handle, Native.INFINITE);
    }

    public void Dispose() => Native.CloseHandle(_handle);
}
