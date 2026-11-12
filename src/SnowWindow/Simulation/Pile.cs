namespace SnowWindow.Simulation;

/// <summary>
/// 가로로 놓인 면(창 윗변 또는 화면 바닥) 위에 쌓인 눈. 칸(ColW 폭)마다 높이를 가진 높이맵이다.
/// 좌표는 모두 가상 화면의 물리 픽셀.
/// </summary>
public sealed class Pile
{
    public Pile(float left, float baseY, int columns, float columnWidth, float maxHeight, float scale, bool isGround)
    {
        Left = left;
        BaseY = baseY;
        ColW = columnWidth;
        H = new float[Math.Max(1, columns)];
        MaxH = maxHeight;
        Scale = scale;
        IsGround = isGround;
        Exposed.Add(new ColumnRange(0, H.Length));
    }

    /// <summary>0번 칸의 왼쪽 x.</summary>
    public float Left { get; internal set; }

    /// <summary>눈이 놓이는 면의 y(창 윗변).</summary>
    public float BaseY { get; internal set; }

    public float ColW { get; }
    public float MaxH { get; }

    /// <summary>창 더미면 그 창 핸들, 화면 바닥이면 0.</summary>
    internal nint Handle { get; init; }

    /// <summary>이번 프레임 창 목록(z-order 위→아래)에서 이 창의 위치. 이보다 앞의 창들이 이 창 위의 것을 가린다.</summary>
    internal int ZIndex { get; set; }

    public float Scale { get; }
    public bool IsGround { get; }

    /// <summary>칸별 눈 높이(px).</summary>
    public float[] H { get; internal set; }

    /// <summary>false면 눈이 내려앉지 않는다(화면 맨 위에 붙은 최대화 창 등).</summary>
    public bool Landable { get; internal set; } = true;

    /// <summary>위쪽 창에 가려지지 않은 칸 범위. 눈은 이 범위에만 내려앉고 그려진다.</summary>
    public List<ColumnRange> Exposed { get; } = new();

    public int Columns => H.Length;
    public float Right => Left + H.Length * ColW;

    /// <summary>쏟아낸 눈 면적 누적(일정량이 모이면 파편 입자 하나로 만든다).</summary>
    internal float ShedAccum;

    /// <summary>이번 프레임에 창이 통째로 옮겨 간 거리(px)와 속도(px/s). 크기 변경은 이동으로 치지 않는다.</summary>
    internal float MoveX;
    internal float MoveSpeed;
    internal float MoveVX;
    internal float MoveVY;

    public int ColumnAt(float x) => (int)MathF.Floor((x - Left) / ColW);

    public bool IsExposed(int column)
    {
        foreach (var r in Exposed)
        {
            if (column >= r.Start && column < r.End)
            {
                return true;
            }
        }

        return false;
    }

    public float TotalArea()
    {
        float sum = 0;
        foreach (var h in H)
        {
            sum += h;
        }

        return sum * ColW;
    }

    public bool HasSnow()
    {
        foreach (var h in H)
        {
            if (h > 0.05f)
            {
                return true;
            }
        }

        return false;
    }
}
