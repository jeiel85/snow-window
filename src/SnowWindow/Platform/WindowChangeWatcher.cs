namespace SnowWindow.Platform;

/// <summary>
/// 최상위 창의 이동·크기 변경·표시/숨김·z-order·최소화·전경 전환을 WinEvent로 감지한다.
/// 창 목록 전체를 매 프레임 훑는 대신, 무언가 바뀌었을 때만 다시 훑게 해 DWM 부하를 줄인다.
/// 콜백은 훅을 건 스레드(엔진 스레드)가 메시지를 처리할 때 그 스레드에서 호출된다.
/// </summary>
internal sealed class WindowChangeWatcher : IDisposable
{
    private readonly Native.WinEventProc _callback;
    private readonly List<nint> _hooks = new();

    public WindowChangeWatcher()
    {
        _callback = OnEvent;
        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);
        Hook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND);
        Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_REORDER); // 파괴·표시·숨김·z-order
        Hook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE);
        Hook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED); // 가상 데스크톱 전환 등
    }

    /// <summary>훅을 하나도 걸지 못했으면 false. 이때는 호출자가 매 프레임 훑어야 한다.</summary>
    public bool IsActive => _hooks.Count > 0;

    private bool Changed { get; set; } = true;

    /// <summary>마지막 호출 이후 창에 변화가 있었는지 돌려주고 표시를 지운다.</summary>
    public bool ConsumeChanged()
    {
        bool changed = Changed;
        Changed = false;
        return changed;
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            Native.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void Hook(uint min, uint max)
    {
        nint hook = Native.SetWinEventHook(min, max, 0, _callback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 커서·캐럿 이동이나 창 안의 개별 요소 이벤트는 무시한다.
        if (hwnd == 0 || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF)
        {
            return;
        }

        // 자식 창(브라우저 내부 등)의 변화도 무시. 이미 파괴돼 판별할 수 없으면 변화로 본다.
        nint root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != 0 && root != hwnd)
        {
            return;
        }

        Changed = true;
    }
}
