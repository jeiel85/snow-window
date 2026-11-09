namespace SnowWindow.Simulation;

/// <summary>물리 픽셀 단위 정수 사각형. Right/Bottom은 포함하지 않는다.</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(float x, float y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>모니터 한 대. Scale은 DPI/96.</summary>
public sealed record MonitorInfo(RectI Bounds, RectI WorkArea, float Scale);

/// <summary>
/// 최상위 창 하나의 스냅샷. 목록은 z-order 위(앞)→아래(뒤) 순서로 넘긴다.
/// HoldsSnow가 false인 창(작업 표시줄, 메뉴 등)은 눈을 받지는 않지만 뒤쪽 창의 눈을 가린다.
/// </summary>
public readonly record struct WindowSnapshot(nint Handle, RectI Bounds, bool HoldsSnow);

public readonly record struct CursorSample(float X, float Y, bool Valid);

public enum SnowIntensity
{
    Light,
    Normal,
    Heavy,
}

/// <summary>쌓인 눈 칸 범위 [Start, End).</summary>
public readonly record struct ColumnRange(int Start, int End);
