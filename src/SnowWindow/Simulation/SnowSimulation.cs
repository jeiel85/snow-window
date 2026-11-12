namespace SnowWindow.Simulation;

/// <summary>떨어지는 눈송이 또는 쌓인 눈에서 튀어나온 파편.</summary>
public struct Particle
{
    public float X;
    public float Y;
    public float VX;
    public float VY;
    public float Radius;
    public float Alpha;

    /// <summary>0(멀리)~1(가까이). 가까운 눈송이일수록 크고 빠르며 창 위에 잘 내려앉는다.</summary>
    public float Depth;

    public float Phase;
    public float SwayAmp;
    public float SwayFreq;
    public float TargetVY;

    /// <summary>내려앉을 때 쌓이는 눈 면적(px²).</summary>
    public float Volume;

    public float Age;
    public bool Debris;
    public int Monitor;
}

/// <summary>
/// 눈 시뮬레이션. Win32에 의존하지 않으며, 모든 좌표는 가상 화면의 물리 픽셀이다.
/// 창 목록·커서는 매 프레임 호출자가 넘겨준다. 눈덩이·눈사람은 SnowSimulation.Snowballs.cs.
/// </summary>
public sealed partial class SnowSimulation
{
    public const int MaxParticles = 12000;

    // 아래 값들은 배율 1(96 DPI) 기준 물리 픽셀이며, 모니터 배율을 곱해 쓴다.
    private const float ColumnWidth = 3f;
    private const float WindowPileMax = 14f;
    private const float GroundPileMax = 18f;
    private const float AccumFactor = 5f;      // 눈송이 하나가 쌓이는 양(눈송이 면적 배수)
    private const float MeltRate = 0.01f;      // px/s
    private const float DebrisArea = 7f;       // 파편 하나가 나르는 눈 면적(px²)
    private const float WindowInset = 4f;      // 둥근 창 모서리만큼 양 끝을 비운다
    private const float AvgFallSpeed = 51f;    // 눈송이 평균 낙하 속도(px/s)

    private readonly Random _rng;
    private readonly Particle[] _p = new Particle[MaxParticles];
    private int _count;

    private readonly List<MonitorState> _monitors = new();
    private readonly Dictionary<nint, Pile> _windowPiles = new();
    private readonly List<Pile> _windowOrder = new();
    private readonly HashSet<nint> _seen = new();
    private readonly List<nint> _gone = new();
    private readonly List<(float A, float B)> _intervals = new();
    private IReadOnlyList<WindowSnapshot> _windows = Array.Empty<WindowSnapshot>(); // 이번 프레임 창 목록

    private float _time;
    private float _cx;
    private float _cy;
    private float _cvx;
    private float _cvy;
    private float _moved; // 이번 프레임에 커서가 움직인 거리(px)
    private bool _cursorValid;
    private bool _mouseInteraction = true;

    public SnowSimulation(int seed)
    {
        _rng = new Random(seed);
    }

    public SnowIntensity Intensity { get; set; } = SnowIntensity.Normal;

    /// <summary>커서로 쌓인 눈을 흩날리고, 문질러 뭉치고, 눈덩이를 잡아 옮기는 기능 전체.</summary>
    public bool MouseInteraction
    {
        get => _mouseInteraction;
        set
        {
            _mouseInteraction = value;
            if (!value)
            {
                ReleaseGrab();
            }
        }
    }

    public ReadOnlySpan<Particle> Particles => _p.AsSpan(0, _count);

    public int ParticleCount => _count;

    /// <summary>이번 프레임에 눈을 받는 창들의 더미, z-order 위→아래.</summary>
    public IReadOnlyList<Pile> WindowPilesTopFirst => _windowOrder;

    public int MonitorCount => _monitors.Count;

    public MonitorInfo GetMonitor(int index) => _monitors[index].Info;

    public Pile GetGround(int index) => _monitors[index].Ground;

    public bool IsSuspended(int index) => _monitors[index].Suspended;

    /// <summary>테스트용: 내려앉을 확률을 고정한다.</summary>
    internal float? LandingChanceOverride { get; set; }

    internal Pile? FindWindowPile(nint handle) => _windowPiles.GetValueOrDefault(handle);

    /// <summary>모니터 구성이 바뀌었으면 상태를 새로 만들고 true를 돌려준다.</summary>
    public bool SetMonitors(IReadOnlyList<MonitorInfo> monitors, bool prefill = true)
    {
        if (monitors.Count == _monitors.Count)
        {
            bool same = true;
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i] != _monitors[i].Info)
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return false;
            }
        }

        _monitors.Clear();
        _windowPiles.Clear();
        _windowOrder.Clear();
        _count = 0;
        ClearSnowballs();

        foreach (var m in monitors)
        {
            float colW = ColumnWidthFor(m.Scale);
            float groundY = m.WorkArea.Bottom < m.Bounds.Bottom && m.WorkArea.Bottom > m.Bounds.Top
                ? m.WorkArea.Bottom
                : m.Bounds.Bottom;
            int cols = (int)MathF.Ceiling(m.Bounds.Width / colW);
            var ground = new Pile(m.Bounds.Left, groundY, cols, colW, GroundPileMax * m.Scale, m.Scale, isGround: true);
            _monitors.Add(new MonitorState(m, ground));
        }

        if (prefill)
        {
            Prefill();
        }

        return true;
    }

    public void SetSuspended(int index, bool suspended) => _monitors[index].Suspended = suspended;

    /// <summary>모든 입자와 쌓인 눈을 지운다.</summary>
    public void Reset()
    {
        _count = 0;
        _windowPiles.Clear();
        _windowOrder.Clear();
        ClearSnowballs();
        foreach (var m in _monitors)
        {
            Array.Clear(m.Ground.H);
            m.Ground.ShedAccum = 0;
            m.SpawnAccum = 0;
        }
    }

    /// <summary>쌓인 눈·눈덩이와 튀어나온 파편만 지운다(내리는 눈은 유지).</summary>
    public void ClearPiles()
    {
        ClearSnowballs();
        foreach (var pile in _windowPiles.Values)
        {
            Array.Clear(pile.H);
            pile.ShedAccum = 0;
        }

        foreach (var m in _monitors)
        {
            Array.Clear(m.Ground.H);
            m.Ground.ShedAccum = 0;
        }

        int i = 0;
        while (i < _count)
        {
            if (_p[i].Debris)
            {
                RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>내리는 눈을 현재 세기에 맞춰 화면 전체에 미리 뿌린다.</summary>
    public void Prefill()
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i].Info;
            float s = m.Scale;
            float area = m.Bounds.Width / s * (m.Bounds.Height / s);
            int n = (int)(Density() * area / (1920f * 1080f));
            for (int k = 0; k < n; k++)
            {
                SpawnFlake(i, prefill: true);
            }
        }
    }

    public void Update(float dt, IReadOnlyList<WindowSnapshot> windows, CursorSample cursor)
    {
        if (_monitors.Count == 0)
        {
            return;
        }

        dt = Math.Clamp(dt, 0f, 0.1f);
        if (dt <= 0f)
        {
            return;
        }

        _time += dt;
        _windows = windows;
        UpdateCursor(cursor, dt);
        SyncWindows(windows, dt);
        Spawn(dt);
        StepParticles(dt);
        StepPiles(dt);
        _gatherPile = null;
        if (MouseInteraction && _cursorValid && _carried == null)
        {
            Gather(dt);
            KickPiles(dt);
        }
        else
        {
            ResetRub();
        }

        StepSnowballs(dt);
    }

    internal void InjectParticle(Particle particle)
    {
        if (_count < MaxParticles)
        {
            _p[_count++] = particle;
        }
    }

    // ───────────────────────── 커서 ─────────────────────────

    private void UpdateCursor(CursorSample cursor, float dt)
    {
        _moved = 0f;
        if (!cursor.Valid)
        {
            _cursorValid = false;
            _cvx = _cvy = 0;
            ReleaseGrab(); // 보안 데스크톱 등으로 커서를 놓쳤다
            return;
        }

        if (_cursorValid)
        {
            _moved = MathF.Sqrt((cursor.X - _cx) * (cursor.X - _cx) + (cursor.Y - _cy) * (cursor.Y - _cy));
            float vx = (cursor.X - _cx) / dt;
            float vy = (cursor.Y - _cy) / dt;
            float sp = MathF.Sqrt(vx * vx + vy * vy);
            const float maxSpeed = 6000f; // 모니터 사이 순간이동 등 튀는 값 제한
            if (sp > maxSpeed)
            {
                vx *= maxSpeed / sp;
                vy *= maxSpeed / sp;
            }

            float a = MathF.Min(1f, dt * 25f);
            _cvx += (vx - _cvx) * a;
            _cvy += (vy - _cvy) * a;
        }

        _cx = cursor.X;
        _cy = cursor.Y;
        _cursorValid = true;
    }

    // ───────────────────────── 창 동기화 ─────────────────────────

    private void SyncWindows(IReadOnlyList<WindowSnapshot> windows, float dt)
    {
        _seen.Clear();
        _windowOrder.Clear();

        for (int z = 0; z < windows.Count; z++)
        {
            var w = windows[z];
            if (!w.HoldsSnow)
            {
                continue;
            }

            int mon = MonitorForEdge(w.Bounds);
            float scale = mon >= 0 ? _monitors[mon].Info.Scale : 1f;
            float colW = ColumnWidthFor(scale);
            float inset = WindowInset * scale;
            float left = w.Bounds.Left + inset;
            int cols = (int)((w.Bounds.Right - inset - left) / colW);
            if (cols < 4)
            {
                continue;
            }

            _seen.Add(w.Handle);
            if (!_windowPiles.TryGetValue(w.Handle, out var pile) || pile.ColW != colW)
            {
                if (pile != null)
                {
                    SpillAll(pile); // 배율이 다른 모니터로 옮겨 갔다
                }

                pile = new Pile(left, w.Bounds.Top, cols, colW, WindowPileMax * scale, scale, isGround: false) { Handle = w.Handle };
                _windowPiles[w.Handle] = pile;
            }
            else
            {
                ApplyWindowChange(pile, left, w.Bounds.Top, cols, dt);
            }

            pile.Landable = mon >= 0
                && w.Bounds.Top > _monitors[mon].Info.Bounds.Top + 2
                && w.Bounds.Top < _monitors[mon].Info.Bounds.Bottom - 2;
            if (!pile.Landable)
            {
                // 화면 맨 위에 붙은(최대화된) 창: 눈은 화면 밖으로 밀려난 것으로 본다.
                Array.Clear(pile.H);
            }

            pile.ZIndex = z;
            ComputeExposure(pile, windows, z);
            _windowOrder.Add(pile);
        }

        _gone.Clear();
        foreach (var handle in _windowPiles.Keys)
        {
            if (!_seen.Contains(handle))
            {
                _gone.Add(handle);
            }
        }

        foreach (var handle in _gone)
        {
            SpillAll(_windowPiles[handle]); // 창이 닫히거나 최소화되면 눈이 쏟아진다
            _windowPiles.Remove(handle);
        }
    }

    private void ApplyWindowChange(Pile pile, float left, float top, int cols, float dt)
    {
        pile.MoveX = pile.MoveSpeed = pile.MoveVX = pile.MoveVY = 0f;
        float dx = left - pile.Left;
        float dy = top - pile.BaseY;
        if (cols == pile.Columns)
        {
            if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f)
            {
                return;
            }

            // 이동: 눈은 창을 따라가되, 빠르게 움직이면 관성으로 털려 나간다.
            pile.Left = left;
            pile.BaseY = top;
            float s = pile.Scale;
            float vx = dx / dt;
            float vy = dy / dt;
            float speed = MathF.Sqrt(vx * vx + vy * vy);
            pile.MoveX = dx;
            pile.MoveSpeed = speed;
            pile.MoveVX = vx;
            pile.MoveVY = vy;
            float threshold = 220f * s;
            if (speed > threshold && pile.HasSnow())
            {
                float frac = Math.Clamp((speed - threshold) / (1600f * s), 0f, 1f) * MathF.Min(1f, dt * 7f);
                float limit = 900f * s;
                float bvx = Math.Clamp(vx * 0.45f, -limit, limit);
                float bvy = Math.Clamp(vy * 0.45f, -limit, limit);
                for (int c = 0; c < pile.Columns; c++)
                {
                    if (pile.H[c] > 0.05f)
                    {
                        Shed(pile, c, pile.H[c] * frac, bvx, bvy - _rng.NextSingle() * 60f * s, 50f * s);
                    }
                }
            }

            return;
        }

        // 크기 변경: 남는 칸은 화면 위 절대 위치를 유지하고, 창 밖으로 벗어난 칸의 눈은 떨어진다.
        int offset = (int)MathF.Round(dx / pile.ColW);
        var old = pile.H;
        var next = new float[cols];
        float oldLeft = pile.Left;
        for (int j = 0; j < old.Length; j++)
        {
            int i = j - offset;
            if (i >= 0 && i < cols)
            {
                next[i] = old[j];
            }
            else if (old[j] > 0.05f && pile.IsExposed(j))
            {
                float x = oldLeft + (j + 0.5f) * pile.ColW;
                float dir = i < 0 ? -1f : 1f;
                ReleaseArea(pile, x, pile.BaseY - old[j] * 0.5f, old[j] * pile.ColW,
                    dir * (20f + 40f * _rng.NextSingle()) * pile.Scale, -10f * pile.Scale, 20f * pile.Scale);
            }
        }

        pile.H = next;
        pile.Left = left;
        pile.BaseY = top;
    }

    private void ComputeExposure(Pile pile, IReadOnlyList<WindowSnapshot> windows, int z)
    {
        _intervals.Clear();
        _intervals.Add((pile.Left, pile.Right));
        float top = pile.BaseY - pile.MaxH - 2f;
        float bottom = pile.BaseY + 1f;
        for (int j = 0; j < z && _intervals.Count > 0; j++)
        {
            var o = windows[j].Bounds;
            if (o.Top >= bottom || o.Bottom <= top || o.Right <= pile.Left || o.Left >= pile.Right)
            {
                continue;
            }

            Intervals.Subtract(_intervals, o.Left, o.Right);
        }

        pile.Exposed.Clear();
        foreach (var (a, b) in _intervals)
        {
            int cs = Math.Max(0, (int)MathF.Ceiling((a - pile.Left) / pile.ColW - 0.001f));
            int ce = Math.Min(pile.Columns, (int)MathF.Floor((b - pile.Left) / pile.ColW + 0.001f));
            if (ce > cs)
            {
                pile.Exposed.Add(new ColumnRange(cs, ce));
            }
        }
    }

    // ───────────────────────── 입자 ─────────────────────────

    private void Spawn(float dt)
    {
        float density = Density();
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            if (m.Suspended)
            {
                continue;
            }

            float s = m.Info.Scale;
            float rate = density * (m.Info.Bounds.Width / s) * AvgFallSpeed / (1920f * 1080f);
            m.SpawnAccum += rate * dt;
            while (m.SpawnAccum >= 1f)
            {
                m.SpawnAccum -= 1f;
                SpawnFlake(i, prefill: false);
            }
        }
    }

    private void SpawnFlake(int monitorIndex, bool prefill)
    {
        if (_count >= MaxParticles)
        {
            return;
        }

        var m = _monitors[monitorIndex].Info;
        float s = m.Scale;
        float t = MathF.Pow(_rng.NextSingle(), 2.2f); // 작은(먼) 눈송이가 훨씬 많다
        ref var p = ref _p[_count++];
        p = default;
        p.Depth = t;
        p.Radius = (0.6f + 2.0f * t) * s;
        p.Alpha = 0.5f + 0.5f * t;
        p.TargetVY = (28f + 75f * t) * s * (0.85f + 0.3f * _rng.NextSingle());
        p.SwayAmp = (6f + 12f * _rng.NextSingle()) * s;
        p.SwayFreq = 0.5f + 1.1f * _rng.NextSingle();
        p.Phase = _rng.NextSingle() * MathF.Tau;
        p.X = m.Bounds.Left + _rng.NextSingle() * m.Bounds.Width;
        p.Y = prefill
            ? m.Bounds.Top + _rng.NextSingle() * m.Bounds.Height
            : m.Bounds.Top - p.Radius - _rng.NextSingle() * 30f * s;
        p.VX = Wind() * s * (0.35f + 0.65f * t);
        p.VY = p.TargetVY;
        p.Volume = MathF.PI * p.Radius * p.Radius * AccumFactor;
        p.Monitor = monitorIndex;
    }

    private void SpawnDebris(float x, float y, float vx, float vy, float area, float scale)
    {
        if (_count >= MaxParticles)
        {
            return;
        }

        int mi = MonitorAt(x, y);
        if (mi < 0)
        {
            mi = NearestMonitor(x, y);
        }

        ref var p = ref _p[_count++];
        p = default;
        p.Debris = true;
        p.X = x;
        p.Y = y;
        p.VX = vx;
        p.VY = vy;
        p.Radius = (0.8f + 0.9f * _rng.NextSingle()) * scale;
        p.Alpha = 0.95f;
        p.Depth = 1f;
        p.Volume = area;
        p.Monitor = mi;
    }

    private void StepParticles(float dt)
    {
        float wind = Wind();
        float cursorSpeed = MathF.Sqrt(_cvx * _cvx + _cvy * _cvy);
        bool push = MouseInteraction && _cursorValid && cursorSpeed > 40f;

        int i = 0;
        while (i < _count)
        {
            ref var p = ref _p[i];
            if (p.Monitor < 0 || p.Monitor >= _monitors.Count)
            {
                RemoveAt(i);
                continue;
            }

            var b = _monitors[p.Monitor].Info.Bounds;
            float s = _monitors[p.Monitor].Info.Scale;
            float prevY = p.Y;

            if (!p.Debris)
            {
                float k = MathF.Min(1f, dt * 1.5f);
                float tvx = wind * s * (0.35f + 0.65f * p.Depth);
                p.VX += (tvx - p.VX) * k;
                p.VY += (p.TargetVY - p.VY) * k;
                if (push)
                {
                    ApplyCursorWind(ref p, s, dt);
                }

                float sway = p.SwayAmp * p.SwayFreq * MathF.Cos(p.Phase + p.Age * p.SwayFreq);
                p.X += (p.VX + sway) * dt;
                p.Y += p.VY * dt;
            }
            else
            {
                p.VY = MathF.Min(p.VY + 950f * s * dt, 520f * s);
                p.VX += (wind * s * 0.5f - p.VX) * MathF.Min(1f, dt * 1.1f);
                if (push)
                {
                    ApplyCursorWind(ref p, s, dt);
                }

                p.X += p.VX * dt;
                p.Y += p.VY * dt;
            }

            p.Age += dt;

            float chance = LandingChanceOverride
                ?? (p.Debris ? 0.8f : (p.Depth < 0.2f ? 0f : 0.12f + 0.7f * p.Depth));
            if (chance > 0f && p.VY > 0f && TryLand(ref p, prevY, chance))
            {
                RemoveAt(i);
                continue;
            }

            if (p.Y > b.Bottom + 10f * s || (p.Debris && p.Age > 15f))
            {
                RemoveAt(i);
                continue;
            }

            if (!p.Debris)
            {
                float margin = 30f * s;
                if (p.X < b.Left - margin)
                {
                    p.X += b.Width + 2f * margin;
                }
                else if (p.X > b.Right + margin)
                {
                    p.X -= b.Width + 2f * margin;
                }
            }
            else if (p.X < b.Left || p.X >= b.Right)
            {
                int mi = MonitorAt(p.X, p.Y);
                if (mi >= 0)
                {
                    p.Monitor = mi;
                }
                else if (p.X < b.Left - 50f * s || p.X > b.Right + 50f * s)
                {
                    RemoveAt(i);
                    continue;
                }
            }

            i++;
        }
    }

    private void ApplyCursorWind(ref Particle p, float s, float dt)
    {
        float dx = p.X - _cx;
        float dy = p.Y - _cy;
        float r = 80f * s;
        float d2 = dx * dx + dy * dy;
        if (d2 >= r * r)
        {
            return;
        }

        float d = MathF.Sqrt(d2) + 0.001f;
        float f = 1f - d / r;
        float a = MathF.Min(1f, dt * 10f) * f;
        p.VX += (_cvx * 0.7f - p.VX) * a + dx / d * 260f * s * a;
        p.VY += (_cvy * 0.7f - p.VY) * a + dy / d * 260f * s * a;
    }

    private bool TryLand(ref Particle p, float prevY, float chance)
    {
        foreach (var pile in _windowOrder)
        {
            int r = TryLandOn(pile, ref p, prevY, chance);
            if (r != 0)
            {
                return r > 0;
            }
        }

        foreach (var m in _monitors)
        {
            int r = TryLandOn(m.Ground, ref p, prevY, chance);
            if (r != 0)
            {
                return r > 0;
            }
        }

        return false;
    }

    /// <summary>1: 내려앉음, -1: 면을 지나쳤지만 앞쪽을 통과함, 0: 이 면과 무관.</summary>
    private int TryLandOn(Pile pile, ref Particle p, float prevY, float chance)
    {
        if (!pile.Landable || p.X < pile.Left || p.X >= pile.Right)
        {
            return 0;
        }

        int c = pile.ColumnAt(p.X);
        if ((uint)c >= (uint)pile.Columns || !pile.IsExposed(c))
        {
            return 0;
        }

        float surface = pile.BaseY - pile.H[c];
        if (prevY >= surface || p.Y < surface)
        {
            return 0;
        }

        if (_rng.NextSingle() < chance)
        {
            Deposit(pile, c, p.Volume);
            return 1;
        }

        return -1;
    }

    private void Deposit(Pile pile, int c, float area)
    {
        int half = 1 + _rng.Next(3);
        float total = area / pile.ColW;
        float weightSum = (half + 1) * (half + 1);
        for (int k = -half; k <= half; k++)
        {
            int idx = c + k;
            if ((uint)idx >= (uint)pile.Columns)
            {
                continue;
            }

            float w = half + 1 - Math.Abs(k);
            pile.H[idx] = MathF.Min(pile.MaxH, pile.H[idx] + total * w / weightSum);
        }
    }

    // ───────────────────────── 쌓인 눈 ─────────────────────────

    private void StepPiles(float dt)
    {
        foreach (var pile in _windowOrder)
        {
            StepPile(pile, dt);
        }

        foreach (var m in _monitors)
        {
            StepPile(m.Ground, dt);
        }
    }

    private void StepPile(Pile pile, float dt)
    {
        var h = pile.H;
        int n = h.Length;
        float melt = MeltRate * pile.Scale * dt;
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (h[i] > 0f)
            {
                h[i] = MathF.Max(0f, h[i] - melt);
                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        // 너무 가파른 곳은 옆으로 흘러내린다(눈사태 완화). 방향 편향을 없애려고 양방향으로 한 번씩.
        float maxStep = pile.ColW * 0.85f;
        for (int i = 0; i < n - 1; i++)
        {
            Relax(h, i, maxStep);
        }

        for (int i = n - 2; i >= 0; i--)
        {
            Relax(h, i, maxStep);
        }

        if (pile.IsGround)
        {
            return;
        }

        // 창 양 끝에서는 일정 높이 이상 버티지 못하고 떨어진다.
        float s = pile.Scale;
        float hold = pile.ColW * 1.3f;
        if (h[0] > hold && pile.IsExposed(0))
        {
            float extra = h[0] - hold;
            h[0] = hold;
            ReleaseArea(pile, pile.Left - 1f, pile.BaseY - hold, extra * pile.ColW,
                -(25f + 50f * _rng.NextSingle()) * s, -10f * s * _rng.NextSingle(), 15f * s);
        }

        if (h[n - 1] > hold && pile.IsExposed(n - 1))
        {
            float extra = h[n - 1] - hold;
            h[n - 1] = hold;
            ReleaseArea(pile, pile.Right + 1f, pile.BaseY - hold, extra * pile.ColW,
                (25f + 50f * _rng.NextSingle()) * s, -10f * s * _rng.NextSingle(), 15f * s);
        }
    }

    private static void Relax(float[] h, int i, float maxStep)
    {
        float d = h[i] - h[i + 1];
        if (d > maxStep)
        {
            float t = (d - maxStep) * 0.5f;
            h[i] -= t;
            h[i + 1] += t;
        }
        else if (d < -maxStep)
        {
            float t = (-d - maxStep) * 0.5f;
            h[i + 1] -= t;
            h[i] += t;
        }
    }

    private void KickPiles(float dt)
    {
        float speed = MathF.Sqrt(_cvx * _cvx + _cvy * _cvy);
        foreach (var pile in _windowOrder)
        {
            if (pile != _gatherPile)
            {
                KickPile(pile, speed, dt);
            }
        }

        foreach (var m in _monitors)
        {
            if (m.Ground != _gatherPile)
            {
                KickPile(m.Ground, speed, dt);
            }
        }
    }

    private void KickPile(Pile pile, float speed, float dt)
    {
        float s = pile.Scale;
        if (speed < 30f * s)
        {
            return;
        }

        float r = 22f * s;
        if (_cx < pile.Left - r || _cx > pile.Right + r
            || _cy < pile.BaseY - pile.MaxH - r || _cy > pile.BaseY + r * 0.7f)
        {
            return;
        }

        int c0 = Math.Max(0, pile.ColumnAt(_cx - r));
        int c1 = Math.Min(pile.Columns - 1, pile.ColumnAt(_cx + r));
        float strength = Math.Clamp(speed / (1000f * s), 0.1f, 0.85f) * MathF.Min(1f, dt * 12f);
        float limit = 1100f * s;
        for (int c = c0; c <= c1; c++)
        {
            float h = pile.H[c];
            if (h < 0.2f || !pile.IsExposed(c))
            {
                continue;
            }

            float colX = pile.Left + (c + 0.5f) * pile.ColW;
            float surface = pile.BaseY - h;
            float dx = colX - _cx;
            float dy = _cy < surface ? surface - _cy : (_cy > pile.BaseY ? _cy - pile.BaseY : 0f);
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= r)
            {
                continue;
            }

            float f = 1f - d / r;
            float vx = _cvx * 0.5f + MathF.Sign(dx) * (20f + 80f * _rng.NextSingle()) * s;
            float vy = -(80f * s + speed * 0.3f) * (0.4f + 0.8f * _rng.NextSingle()) + MathF.Min(0f, _cvy * 0.5f);
            Shed(pile, c, h * strength * f, Math.Clamp(vx, -limit, limit), Math.Max(vy, -limit), 35f * s);
        }
    }

    /// <summary>c번 칸에서 height만큼 눈을 덜어 파편으로 날린다.</summary>
    private void Shed(Pile pile, int c, float height, float vx, float vy, float spread)
    {
        float take = MathF.Min(height, pile.H[c]);
        if (take <= 0f)
        {
            return;
        }

        pile.H[c] -= take;
        float x = pile.Left + (c + _rng.NextSingle()) * pile.ColW;
        float y = pile.BaseY - pile.H[c] - _rng.NextSingle() * take;
        ReleaseArea(pile, x, y, take * pile.ColW, vx, vy, spread);
    }

    private void ReleaseArea(Pile pile, float x, float y, float area, float vx, float vy, float spread)
    {
        float unit = DebrisArea * pile.Scale * pile.Scale;
        pile.ShedAccum += area;
        while (pile.ShedAccum >= unit)
        {
            pile.ShedAccum -= unit;
            SpawnDebris(
                x + (_rng.NextSingle() * 2f - 1f) * pile.ColW,
                y,
                vx + (_rng.NextSingle() * 2f - 1f) * spread,
                vy + (_rng.NextSingle() * 2f - 1f) * spread,
                unit,
                pile.Scale);
        }
    }

    /// <summary>창이 사라질 때: 보이던 눈은 쏟아지고, 다른 창에 가려 있던 눈은 그냥 사라진다.</summary>
    private void SpillAll(Pile pile)
    {
        float s = pile.Scale;
        for (int c = 0; c < pile.Columns; c++)
        {
            if (pile.H[c] <= 0.05f)
            {
                continue;
            }

            if (pile.IsExposed(c))
            {
                Shed(pile, c, pile.H[c], (_rng.NextSingle() * 2f - 1f) * 30f * s, -_rng.NextSingle() * 40f * s, 25f * s);
            }
            else
            {
                pile.H[c] = 0f;
            }
        }
    }

    // ───────────────────────── 보조 ─────────────────────────

    private float Density() => Intensity switch
    {
        SnowIntensity.Light => 160f,
        SnowIntensity.Heavy => 1000f,
        _ => 420f,
    };

    /// <summary>천천히 바뀌는 바람(px/s, 배율 1 기준).</summary>
    private float Wind() =>
        14f * MathF.Sin(_time * 0.05f)
        + 9f * MathF.Sin(_time * 0.13f + 1.7f)
        + 5f * MathF.Sin(_time * 0.37f + 0.3f);

    private static float ColumnWidthFor(float scale) => MathF.Max(2f, MathF.Round(ColumnWidth * scale));

    private int MonitorAt(float x, float y)
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Info.Bounds.Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private int NearestMonitor(float x, float y)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < _monitors.Count; i++)
        {
            var b = _monitors[i].Info.Bounds;
            float cx = Math.Clamp(x, b.Left, b.Right);
            float cy = Math.Clamp(y, b.Top, b.Bottom);
            float d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>창 윗변이 놓인 모니터(가운데→양 끝 순으로 확인).</summary>
    private int MonitorForEdge(RectI b)
    {
        int mi = MonitorAt((b.Left + b.Right) * 0.5f, b.Top);
        if (mi < 0)
        {
            mi = MonitorAt(b.Left + 20f, b.Top);
        }

        if (mi < 0)
        {
            mi = MonitorAt(b.Right - 20f, b.Top);
        }

        return mi;
    }

    private void RemoveAt(int i) => _p[i] = _p[--_count];

    private sealed class MonitorState(MonitorInfo info, Pile ground)
    {
        public MonitorInfo Info { get; } = info;
        public Pile Ground { get; } = ground;
        public float SpawnAccum { get; set; }
        public bool Suspended { get; set; }
    }
}
