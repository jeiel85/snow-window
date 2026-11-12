namespace SnowWindow.Simulation;

/// <summary>
/// 눈덩이와 눈사람. 쌓인 눈 위에서 커서를 문지르면 그 자리 눈이 뭉쳐 눈덩이가 되고 커진다.
/// 눈덩이는 잡아서 옮기거나 던질 수 있고(잡기·놓기는 호출자가 TryGrab/ReleaseGrab으로 알린다),
/// 눈덩이 위에 눈덩이를 올리면 눈사람이 된다.
/// </summary>
public sealed partial class SnowSimulation
{
    public const int MaxSnowballs = 24;

    // 아래 값들은 배율 1(96 DPI) 기준 물리 픽셀이며, 모니터 배율을 곱해 쓴다.
    private const float RubRadius = 36f;         // 커서가 이 범위 안에서 오가야 문지르는 것으로 본다
    private const float RubTrigger = 70f;        // 이 범위 안에서 이만큼 움직이면 뭉치기 시작
    private const float RubDecay = 1f;           // 1/s. 멈추면 문지른 양이 줄어든다
    private const float RubFollow = 4f;          // 1/s. 문지르는 중심이 커서를 따라가는 빠르기
    private const float GatherBand = 16f;        // 눈 표면에서 이만큼 위까지 문지르는 자리로 본다
    private const float GatherNear = 20f;        // 이미 있는 눈덩이 가장자리에서 이 거리 안이면 그 눈덩이를 키운다
    private const float GatherReach = 10f;       // 눈덩이 가장자리 바깥으로 이만큼까지 눈을 끌어온다
    private const float GatherHand = 8f;         // 커서 양옆으로 이만큼 안의 눈도 끌어온다
    private const float GatherDistance = 40f;    // 커서가 이만큼 움직이면 손 닿는 눈의 GatherScoop만큼 떠낸다
    private const float GatherScoop = 0.35f;
    private const float CreateReach = 12f;       // 처음 뭉칠 때 커서 양옆으로 떠내는 범위
    private const float CreateScoop = 0.6f;
    private const float MinCreateRadius = 3.5f;  // 이만큼 떠낼 눈이 없으면 눈덩이가 생기지 않는다
    private const float MinBallRadius = 1.5f;    // 녹아서 이보다 작아지면 없어진다
    private const float MaxBallRadius = 26f;
    private const float PlowScoop = 0.4f;        // 들고 굴릴 때 눈덩이에 닿은 눈을 떠내는 비율(지름만큼 움직일 때)
    private const float BallSink = 0.12f;        // 쌓인 눈에 반지름의 이만큼 파묻혀 앉는다
    private const float StackOverlap = 0.9f;     // 위아래 눈덩이 중심 거리 = 두 반지름 합 × 이 값
    private const float StackCatch = 0.8f;       // 아래 눈덩이 중심에서 반지름의 이 비율 안에 떨어져야 올라앉는다
    private const float BallGravity = 1400f;
    private const float BallTerminal = 1000f;    // 떨어뜨리기만 해서는 SplatSpeed에 닿지 않는다
    private const float ThrowLimit = 2200f;
    private const float SplatSpeed = 1500f;      // 이보다 세게 부딪히면 부서져 흩어진다
    private const float BallShakeSpeed = 700f;   // 창을 이보다 빨리 움직이면 위에 놓인 눈덩이가 떨어진다

    private static readonly Comparison<Snowball> LowestFirst = (a, b) => b.Y.CompareTo(a.Y);

    private readonly List<Snowball> _balls = new();
    private Snowball? _carried;
    private Pile? _gatherPile; // 이번 프레임에 문질러 뭉치는 중인 더미(여기서는 흩날리지 않는다)
    private Pile? _rubPile;
    private float _rubX;
    private float _rubY;
    private float _rubPath;

    /// <summary>아래쪽(화면에서 낮은) 눈덩이부터. 이 순서로 그리면 머리가 몸통 위에 온다.</summary>
    public IReadOnlyList<Snowball> Snowballs => _balls;

    public bool IsCarrying => _carried != null;

    /// <summary>그 점에 보이는 눈덩이(여럿이면 위에 그려진 것). 마우스 기능이 꺼져 있거나 일시 정지된 모니터면 null.</summary>
    public Snowball? SnowballAt(float x, float y)
    {
        if (!MouseInteraction)
        {
            return null;
        }

        int mi = MonitorAt(x, y);
        if (mi < 0 || _monitors[mi].Suspended)
        {
            return null;
        }

        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            float r = b.GrabRadius;
            float dx = x - b.X;
            float dy = y - b.Y;
            if (b.Visible && !b.Removed && dx * dx + dy * dy <= r * r)
            {
                return b;
            }
        }

        return null;
    }

    /// <summary>(x, y)의 눈덩이를 잡는다. 놓을 때까지 커서를 따라간다.</summary>
    public bool TryGrab(float x, float y)
    {
        if (_carried != null)
        {
            return true;
        }

        var b = SnowballAt(x, y);
        if (b == null)
        {
            return false;
        }

        b.State = SnowballState.Carried;
        b.Pile = null;
        b.Below = null;
        b.GrabDX = b.X - x;
        b.GrabDY = b.Y - y;
        b.VX = b.VY = 0f;
        _carried = b;
        ResetRub();
        return true;
    }

    /// <summary>잡고 있던 눈덩이를 놓는다. 커서가 움직이던 속도로 날아간다.</summary>
    public void ReleaseGrab()
    {
        var b = _carried;
        _carried = null;
        if (b == null || b.Removed)
        {
            return;
        }

        float limit = ThrowLimit * b.Scale;
        b.State = SnowballState.Falling;
        b.VX = Math.Clamp(_cvx, -limit, limit);
        b.VY = Math.Clamp(_cvy, -limit, limit);
    }

    /// <summary>테스트용: 떨어지는 눈덩이를 넣는다.</summary>
    internal Snowball AddSnowball(float x, float y, float radius)
    {
        int mi = MonitorAt(x, y);
        float scale = mi >= 0 ? _monitors[mi].Info.Scale : 1f;
        var b = new Snowball(x, y, radius, scale);
        _balls.Add(b);
        return b;
    }

    private void ClearSnowballs()
    {
        _balls.Clear();
        _carried = null;
        ResetRub();
    }

    // ───────────────────────── 문질러 뭉치기 ─────────────────────────

    private void ResetRub()
    {
        _rubPile = null;
        _rubPath = 0f;
    }

    private void Gather(float dt)
    {
        _rubPath *= MathF.Exp(-RubDecay * dt);
        var pile = FindGatherPile(_cx, _cy);
        if (pile == null)
        {
            ResetRub();
            return;
        }

        float s = pile.Scale;
        if (pile != _rubPile)
        {
            _rubPile = pile;
            _rubX = _cx;
            _rubY = _cy;
            _rubPath = 0f;
            return;
        }

        // 중심은 커서를 천천히 따라간다. 한쪽으로 쭉 쓸고 가면 중심에서 멀어져 문지르기가 끊기고(흩날리기),
        // 좁은 범위를 오가면 중심이 제자리에 머물러 움직인 거리가 쌓인다(뭉치기).
        float a = MathF.Min(1f, dt * RubFollow);
        _rubX += (_cx - _rubX) * a;
        _rubY += (_cy - _rubY) * a;
        float dx = _cx - _rubX;
        float dy = _cy - _rubY;
        float radius = RubRadius * s;
        if (dx * dx + dy * dy > radius * radius)
        {
            _rubX = _cx;
            _rubY = _cy;
            _rubPath = 0f;
            return;
        }

        _rubPath += _moved;
        if (_rubPath < RubTrigger * s || _moved <= 0f)
        {
            return;
        }

        // 문지르는 중심이 다른 창에 가려진 칸이면 커서 자리를 쓴다(눈덩이가 가려진 곳에 생기거나 굴러 들어가지 않게).
        float centerX = Math.Clamp(_rubX, pile.Left, pile.Right - 0.01f);
        if (!pile.IsExposed(pile.ColumnAt(centerX)))
        {
            centerX = _cx;
        }

        var ball = FindGatherBall(pile, centerX) ?? TryCreateSnowball(pile, centerX);
        if (ball == null)
        {
            return; // 뭉칠 만큼 눈이 없다
        }

        _gatherPile = pile;

        // 문지르는 중심 쪽으로 굴러가며(쓸고 지나가면 따라 굴러간다) 주변 눈을 빨아들인다.
        ball.X += (centerX - ball.X) * MathF.Min(1f, dt * 2.5f);
        float need = MaxArea(ball) - ball.Area;
        if (need <= 0f)
        {
            return;
        }

        // 눈덩이 둘레와 커서가 문지르는 자리의 눈을 끌어온다.
        float reach = ball.R + GatherReach * s;
        float x0 = MathF.Min(ball.X - reach, _cx - GatherHand * s);
        float x1 = MathF.Max(ball.X + reach, _cx + GatherHand * s);
        float scoop = GatherScoop * MathF.Min(1f, _moved / (GatherDistance * s));
        float available = SnowWithin(pile, x0, x1) * scoop;
        if (available > need)
        {
            scoop *= need / available;
        }

        ball.SetArea(ball.Area + TakeSnow(pile, x0, x1, scoop));
    }

    /// <summary>커서가 문지르는 자리(눈 표면 바로 위, 또는 그 더미에 놓인 눈덩이 위)인 더미. 앞쪽 창부터 본다.</summary>
    private Pile? FindGatherPile(float x, float y)
    {
        foreach (var pile in _windowOrder)
        {
            if (InGatherZone(pile, x, y))
            {
                return pile;
            }
        }

        foreach (var m in _monitors)
        {
            if (InGatherZone(m.Ground, x, y))
            {
                return m.Ground;
            }
        }

        return null;
    }

    private bool InGatherZone(Pile pile, float x, float y)
    {
        if (!pile.Landable || x < pile.Left || x >= pile.Right)
        {
            return false;
        }

        int c = pile.ColumnAt(x);
        float s = pile.Scale;
        if ((uint)c >= (uint)pile.Columns || !pile.IsExposed(c) || y > pile.BaseY + 3f * s)
        {
            return false;
        }

        if (y >= pile.BaseY - pile.H[c] - GatherBand * s)
        {
            return true;
        }

        foreach (var b in _balls)
        {
            if (b.State == SnowballState.Resting && b.Pile == pile && b.Below == null)
            {
                float r = b.R + 8f * s;
                float dx = x - b.X;
                float dy = y - b.Y;
                if (dx * dx + dy * dy <= r * r)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Snowball? FindGatherBall(Pile pile, float x)
    {
        Snowball? best = null;
        float bestD = float.MaxValue;
        foreach (var b in _balls)
        {
            if (b.State != SnowballState.Resting || b.Pile != pile || b.Below != null || b.Removed)
            {
                continue;
            }

            float d = MathF.Abs(b.X - x) - b.R;
            if (d < GatherNear * pile.Scale && d < bestD)
            {
                best = b;
                bestD = d;
            }
        }

        return best;
    }

    private Snowball? TryCreateSnowball(Pile pile, float x)
    {
        if (_balls.Count >= MaxSnowballs)
        {
            return null;
        }

        float s = pile.Scale;
        float reach = CreateReach * s;
        float min = MathF.PI * (MinCreateRadius * s) * (MinCreateRadius * s);
        if (SnowWithin(pile, x - reach, x + reach) * CreateScoop < min)
        {
            return null;
        }

        var b = new Snowball(x, pile.BaseY, 0f, s)
        {
            State = SnowballState.Resting,
        };
        b.Pile = pile;
        b.SetArea(TakeSnow(pile, x - reach, x + reach, CreateScoop));
        b.Y = RestY(pile, b);
        _balls.Add(b);
        return b;
    }

    /// <summary>[x0, x1] 안 보이는 칸의 눈 면적(px²).</summary>
    private static float SnowWithin(Pile pile, float x0, float x1)
    {
        int c0 = Math.Max(0, pile.ColumnAt(x0));
        int c1 = Math.Min(pile.Columns - 1, pile.ColumnAt(x1));
        float sum = 0f;
        for (int c = c0; c <= c1; c++)
        {
            if (pile.IsExposed(c))
            {
                sum += pile.H[c];
            }
        }

        return sum * pile.ColW;
    }

    /// <summary>[x0, x1] 안 보이는 칸에서 높이의 fraction만큼 덜어내고 덜어낸 면적(px²)을 돌려준다.</summary>
    private static float TakeSnow(Pile pile, float x0, float x1, float fraction)
    {
        int c0 = Math.Max(0, pile.ColumnAt(x0));
        int c1 = Math.Min(pile.Columns - 1, pile.ColumnAt(x1));
        float taken = 0f;
        for (int c = c0; c <= c1; c++)
        {
            if (pile.IsExposed(c))
            {
                float t = pile.H[c] * fraction;
                pile.H[c] -= t;
                taken += t;
            }
        }

        return taken * pile.ColW;
    }

    private static float MaxArea(Snowball b)
    {
        float r = MaxBallRadius * b.Scale;
        return MathF.PI * r * r;
    }

    // ───────────────────────── 움직임 ─────────────────────────

    private void StepSnowballs(float dt)
    {
        if (_balls.Count == 0)
        {
            return;
        }

        // 받치는 쪽(아래)부터 처리해야 위에 놓인 눈덩이가 같은 프레임의 위치를 따라간다.
        _balls.Sort(LowestFirst);
        foreach (var b in _balls)
        {
            b.R -= MeltRate * b.Scale * dt;
            if (b.R < MinBallRadius * b.Scale)
            {
                b.Removed = true;
                continue;
            }

            switch (b.State)
            {
                case SnowballState.Carried:
                    StepCarried(b);
                    break;
                case SnowballState.Resting:
                    StepResting(b, dt);
                    break;
            }

            if (b.State == SnowballState.Falling)
            {
                StepFalling(b, dt);
            }
        }

        SeparateResting();
        int removed = _balls.RemoveAll(b => b.Removed);
        if (removed > 0 && _carried is { Removed: true })
        {
            _carried = null;
        }

        AssignRoles();
        UpdateVisibility();
    }

    private void StepCarried(Snowball b)
    {
        b.X = _cx + b.GrabDX;
        b.Y = _cy + b.GrabDY;
        b.Visible = true;
        if (_moved > 0f)
        {
            Plow(b);
        }
    }

    /// <summary>들고 쌓인 눈 속을 지나가면 닿은 눈이 붙어 커진다(굴려서 키우기).</summary>
    private void Plow(Snowball b)
    {
        float need = MaxArea(b) - b.Area;
        if (need <= 0f)
        {
            return;
        }

        float scoop = PlowScoop * MathF.Min(1f, _moved / (2f * b.R));
        float gained = 0f;
        foreach (var pile in _windowOrder)
        {
            gained += PlowPile(pile, b, scoop);
        }

        foreach (var m in _monitors)
        {
            gained += PlowPile(m.Ground, b, scoop);
        }

        b.SetArea(MathF.Min(b.Area + gained, MaxArea(b)));
    }

    private static float PlowPile(Pile pile, Snowball b, float scoop)
    {
        if (!pile.Landable || b.X + b.R < pile.Left || b.X - b.R >= pile.Right
            || b.Y + b.R < pile.BaseY - pile.MaxH || b.Y - b.R > pile.BaseY)
        {
            return 0f;
        }

        int c0 = Math.Max(0, pile.ColumnAt(b.X - b.R));
        int c1 = Math.Min(pile.Columns - 1, pile.ColumnAt(b.X + b.R));
        float taken = 0f;
        for (int c = c0; c <= c1; c++)
        {
            float h = pile.H[c];
            if (h <= 0f || !pile.IsExposed(c))
            {
                continue;
            }

            float dx = pile.Left + (c + 0.5f) * pile.ColW - b.X;
            float half = b.R * b.R - dx * dx;
            if (half <= 0f)
            {
                continue;
            }

            float bottom = b.Y + MathF.Sqrt(half);
            float depth = MathF.Min(h, bottom - (pile.BaseY - h));
            if (depth > 0f)
            {
                float t = depth * scoop;
                pile.H[c] -= t;
                taken += t;
            }
        }

        return taken * pile.ColW;
    }

    private void StepResting(Snowball b, float dt)
    {
        if (b.Below != null)
        {
            var under = b.Below;
            if (under.Removed || under.State != SnowballState.Resting)
            {
                Fall(b);
                return;
            }

            b.OffsetX *= MathF.Exp(-dt * 6f); // 올려놓은 자리에서 가운데로 자리 잡는다
            b.X = under.X + b.OffsetX;
            b.Y = under.Y - (under.R + b.R) * StackOverlap;
            return;
        }

        var pile = b.Pile;
        if (pile == null || !pile.Landable || !IsCurrent(pile))
        {
            Fall(b); // 창이 닫히거나 최소화됐다
            return;
        }

        b.X += pile.MoveX;
        if (pile.MoveSpeed > BallShakeSpeed * b.Scale)
        {
            // 창을 세게 흔들면 털려 나간다.
            float limit = 900f * b.Scale;
            Fall(b);
            b.VX = Math.Clamp(pile.MoveVX * 0.45f, -limit, limit);
            b.VY = Math.Clamp(pile.MoveVY * 0.45f, -limit, limit) - 80f * b.Scale;
            return;
        }

        if (b.X < pile.Left || b.X >= pile.Right)
        {
            Fall(b); // 창이 줄어들어 가장자리 밖으로 나갔다
            return;
        }

        b.Y = RestY(pile, b);
    }

    private void StepFalling(Snowball b, float dt)
    {
        float s = b.Scale;
        float prevY = b.Y;
        float terminal = BallTerminal * s;
        b.VY = b.VY < terminal
            ? MathF.Min(b.VY + BallGravity * s * dt, terminal)
            : b.VY + (terminal - b.VY) * MathF.Min(1f, dt * 2f); // 세게 던진 눈덩이는 서서히 느려진다
        b.VX *= MathF.Exp(-dt * 0.6f);
        b.X += b.VX * dt;
        b.Y += b.VY * dt;
        b.Visible = true;
        KeepOnScreen(b);
        if (b.VY <= 0f)
        {
            return; // 올라가는 중
        }

        // 이번 프레임에 지나친 면 가운데 가장 먼저 닿는(가장 높은) 곳에 내려앉는다.
        float contact = float.MaxValue;
        Pile? landPile = null;
        Snowball? landBall = null;
        foreach (var o in _balls)
        {
            if (o == b || o.Removed || o.State != SnowballState.Resting || !o.Visible)
            {
                continue;
            }

            float dx = b.X - o.X;
            float reach = (o.R + b.R) * StackOverlap;
            if (MathF.Abs(dx) >= o.R + b.R)
            {
                continue;
            }

            if (MathF.Abs(dx) < o.R * StackCatch)
            {
                float y = o.Y - reach;
                if (prevY <= y + 0.5f && b.Y >= y && y < contact)
                {
                    contact = y;
                    landBall = o;
                    landPile = null;
                }
            }
            else if (b.Y > o.Y - reach && b.Y < o.Y + o.R)
            {
                // 어깨에 걸쳤다: 옆으로 굴러떨어진다.
                float dir = MathF.Sign(dx);
                b.X = o.X + dir * (o.R + b.R) * StackOverlap;
                b.VX = dir * MathF.Max(MathF.Abs(b.VX), 120f * s);
            }
        }

        foreach (var pile in _windowOrder)
        {
            TryContact(pile, b, prevY, ref contact, ref landPile, ref landBall);
        }

        foreach (var m in _monitors)
        {
            TryContact(m.Ground, b, prevY, ref contact, ref landPile, ref landBall);
        }

        if (landPile == null && landBall == null)
        {
            return;
        }

        if (MathF.Sqrt(b.VX * b.VX + b.VY * b.VY) > SplatSpeed * s)
        {
            b.Y = contact;
            Splat(b);
            return;
        }

        b.State = SnowballState.Resting;
        b.VX = b.VY = 0f;
        b.Y = contact;
        b.Pile = landPile;
        b.Below = landBall;
        b.OffsetX = landBall != null ? b.X - landBall.X : 0f;
    }

    private static void TryContact(Pile pile, Snowball b, float prevY, ref float contact, ref Pile? landPile, ref Snowball? landBall)
    {
        if (!pile.Landable || b.X < pile.Left || b.X >= pile.Right || !pile.IsExposed(pile.ColumnAt(b.X)))
        {
            return;
        }

        float y = RestY(pile, b);
        if (prevY <= y + 0.5f && b.Y >= y && y < contact)
        {
            contact = y;
            landPile = pile;
            landBall = null;
        }
    }

    /// <summary>쌓인 눈 위에 앉았을 때의 중심 y. 밑에 깔린 눈에 살짝 파묻히되, 눈이 없으면 면 위에 그대로 놓인다.</summary>
    private static float RestY(Pile pile, Snowball b)
    {
        int c0 = Math.Max(0, pile.ColumnAt(b.X - b.R * 0.4f));
        int c1 = Math.Min(pile.Columns - 1, pile.ColumnAt(b.X + b.R * 0.4f));
        float sum = 0f;
        for (int c = c0; c <= c1; c++)
        {
            sum += pile.H[c];
        }

        float h = c1 >= c0 ? sum / (c1 - c0 + 1) : 0f;
        return pile.BaseY - h - b.R + MathF.Min(b.R * BallSink, h);
    }

    private void Fall(Snowball b)
    {
        b.State = SnowballState.Falling;
        b.Pile = null;
        b.Below = null;
        b.VX = b.VY = 0f;
    }

    /// <summary>화면 밖으로 던진 눈덩이를 가까운 모니터 안으로 되돌린다(위로 던진 것은 다시 떨어지게 둔다).</summary>
    private void KeepOnScreen(Snowball b)
    {
        if (MonitorAt(b.X, b.Y) >= 0)
        {
            return;
        }

        var m = _monitors[NearestMonitor(b.X, b.Y)];
        var bounds = m.Info.Bounds;
        if (b.X < bounds.Left || b.X >= bounds.Right)
        {
            b.X = Math.Clamp(b.X, bounds.Left + 1f, bounds.Right - 1f);
            b.VX = -b.VX * 0.3f;
        }

        if (b.Y >= bounds.Bottom)
        {
            b.Y = RestY(m.Ground, b); // 바닥을 뚫고 나갔으면 바닥 위에 올려 둔다
            b.VY = 0f;
            b.State = SnowballState.Resting;
            b.Pile = m.Ground;
        }
    }

    /// <summary>세게 부딪힌 눈덩이는 부서져 파편으로 흩어진다.</summary>
    private void Splat(Snowball b)
    {
        float s = b.Scale;
        float unit = DebrisArea * s * s;
        int n = Math.Min(400, (int)(b.Area / unit));
        for (int i = 0; i < n; i++)
        {
            float angle = MathF.PI * (1f + _rng.NextSingle()); // 위쪽 반원
            float speed = (80f + 220f * _rng.NextSingle()) * s;
            SpawnDebris(
                b.X + (_rng.NextSingle() * 2f - 1f) * b.R * 0.7f,
                b.Y + (_rng.NextSingle() * 2f - 1f) * b.R * 0.7f,
                MathF.Cos(angle) * speed + b.VX * 0.15f,
                MathF.Sin(angle) * speed,
                unit,
                s);
        }

        b.Removed = true;
    }

    /// <summary>같은 더미 위에 나란히 앉은 눈덩이가 겹치지 않게 옆으로 민다.</summary>
    private void SeparateResting()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            var a = _balls[i];
            if (a.Removed || a.State != SnowballState.Resting || a.Pile == null)
            {
                continue;
            }

            for (int j = i + 1; j < _balls.Count; j++)
            {
                var b = _balls[j];
                if (b.Removed || b.State != SnowballState.Resting || b.Pile != a.Pile)
                {
                    continue;
                }

                float dx = b.X - a.X;
                float min = (a.R + b.R) * StackOverlap;
                float overlap = min - MathF.Abs(dx);
                if (overlap > 0f)
                {
                    float dir = dx >= 0f ? 1f : -1f;
                    a.X -= dir * overlap * 0.5f;
                    b.X += dir * overlap * 0.5f;
                }
            }
        }
    }

    private void AssignRoles()
    {
        foreach (var b in _balls)
        {
            b.Role = SnowballRole.Plain;
            b.HasBallOnTop = false;
        }

        foreach (var b in _balls)
        {
            if (b.State == SnowballState.Resting && b.Below != null)
            {
                b.Below.HasBallOnTop = true;
            }
        }

        foreach (var b in _balls)
        {
            if (b.State == SnowballState.Resting && b.Below != null && !b.HasBallOnTop)
            {
                b.Role = SnowballRole.Head;
                b.Below.Role = SnowballRole.Body;
            }
        }
    }

    /// <summary>
    /// 창 위에 놓인 눈덩이는 그 창보다 앞쪽 창에 가려지면 숨긴다(그리지도, 잡지도 않는다).
    /// 받치는 칸이 가려졌는지만 보면 눈 더미 높이 띠만 따지게 되어, 그보다 높이 솟은 눈덩이·머리·팔이
    /// 앞쪽 창 위에 그려지고 그 창으로 갈 클릭을 가로챈다. 그래서 눈덩이가 그려지는 사각형 전체로 판정한다.
    /// 화면 바닥 눈덩이는 바닥 눈과 마찬가지로 모든 창 앞에 있다.
    /// </summary>
    private void UpdateVisibility()
    {
        foreach (var b in _balls) // 아래쪽부터: 받치는 눈덩이가 먼저 정해진다
        {
            if (b.State != SnowballState.Resting)
            {
                b.Visible = true;
                b.SupportZ = -1;
                continue;
            }

            if (b.Below != null)
            {
                b.SupportZ = b.Below.SupportZ;
                b.Visible = b.Below.Visible && !CoveredByWindowsAbove(b);
                continue;
            }

            var pile = b.Pile!;
            b.SupportZ = pile.IsGround ? -1 : pile.ZIndex;
            b.Visible = pile.IsExposed(pile.ColumnAt(b.X)) && !CoveredByWindowsAbove(b);
        }
    }

    private bool CoveredByWindowsAbove(Snowball b)
    {
        int z = Math.Min(b.SupportZ, _windows.Count);
        if (z <= 0)
        {
            return false;
        }

        float half = b.Role == SnowballRole.Body ? b.R * Snowball.ArmSpan : b.R;
        float left = b.X - half;
        float right = b.X + half;
        float top = b.Y - b.R;
        float bottom = b.Y + b.R;
        for (int j = 0; j < z; j++)
        {
            var o = _windows[j].Bounds;
            if (o.Left < right && o.Right > left && o.Top < bottom && o.Bottom > top)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCurrent(Pile pile)
    {
        if (pile.IsGround)
        {
            foreach (var m in _monitors)
            {
                if (m.Ground == pile)
                {
                    return true;
                }
            }

            return false;
        }

        return _windowPiles.TryGetValue(pile.Handle, out var current) && current == pile;
    }
}
