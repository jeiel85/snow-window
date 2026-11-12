namespace SnowWindow.Simulation;

public enum SnowballState
{
    /// <summary>공중에서 떨어지는 중(던졌거나, 받치던 창·눈덩이가 사라졌다).</summary>
    Falling,

    /// <summary>쌓인 눈(창 윗변·화면 바닥) 또는 다른 눈덩이 위에 놓여 있다.</summary>
    Resting,

    /// <summary>마우스로 잡고 있다.</summary>
    Carried,
}

/// <summary>그릴 때의 모양. 눈덩이 위에 눈덩이를 올리면 눈사람이 된다.</summary>
public enum SnowballRole
{
    Plain,

    /// <summary>눈덩이 위에 놓인 맨 위 눈덩이: 눈과 당근 코를 그린다.</summary>
    Head,

    /// <summary>머리 바로 아래 눈덩이: 나뭇가지 팔을 그린다.</summary>
    Body,
}

/// <summary>쌓인 눈을 뭉쳐 만든 눈덩이. 좌표는 가상 화면의 물리 픽셀(중심과 반지름).</summary>
public sealed class Snowball
{
    internal Snowball(float x, float y, float radius, float scale)
    {
        X = x;
        Y = y;
        R = radius;
        Scale = scale;
    }

    public float X { get; internal set; }
    public float Y { get; internal set; }
    public float R { get; internal set; }
    public float Scale { get; }

    public SnowballState State { get; internal set; } = SnowballState.Falling;
    public SnowballRole Role { get; internal set; }

    /// <summary>false면 받치는 창 윗변이 더 위쪽 창에 가려져 있어 그리지도, 잡지도 않는다.</summary>
    public bool Visible { get; internal set; } = true;

    public float Area => MathF.PI * R * R;

    /// <summary>마우스로 잡을 수 있는 반경(눈덩이보다 조금 넉넉하게).</summary>
    public float GrabRadius => R + 2f * Scale;

    internal float VX;
    internal float VY;

    /// <summary>놓여 있는 쌓인 눈(Resting일 때).</summary>
    internal Pile? Pile;

    /// <summary>받치는 눈덩이(Resting일 때). OffsetX는 그 중심에서 벗어난 정도이며 서서히 0으로 모인다.</summary>
    internal Snowball? Below;
    internal float OffsetX;

    /// <summary>잡은 지점에서 중심까지의 거리.</summary>
    internal float GrabDX;
    internal float GrabDY;

    internal bool Removed;
    internal bool HasBallOnTop;

    internal void SetArea(float area) => R = MathF.Sqrt(MathF.Max(0f, area) / MathF.PI);
}
