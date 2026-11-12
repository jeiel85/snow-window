using SnowWindow.Simulation;

namespace SnowWindow.Tests;

public class SnowballTests
{
    private const float Dt = 1f / 60f;
    private const float GroundY = 1032f;
    private static readonly MonitorInfo Monitor = new(new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1032), 1f);
    private static readonly CursorSample NoCursor = new(0, 0, false);
    private static readonly WindowSnapshot[] NoWindows = Array.Empty<WindowSnapshot>();

    private static SnowSimulation NewSim()
    {
        var sim = new SnowSimulation(seed: 4321) { Intensity = SnowIntensity.Light };
        sim.SetMonitors(new[] { Monitor }, prefill: false);
        return sim;
    }

    private static void Run(SnowSimulation sim, int frames, IReadOnlyList<WindowSnapshot> windows, CursorSample? cursor = null)
    {
        for (int i = 0; i < frames; i++)
        {
            sim.Update(Dt, windows, cursor ?? NoCursor);
        }
    }

    /// <summary>x 주변을 좌우 ±15px로 초당 300px 속도로 오가는 커서.</summary>
    private static CursorSample Rub(int frame, float x, float y)
    {
        int phase = frame % 12;
        float dx = phase < 6 ? -15f + phase * 5f : 15f - (phase - 6) * 5f;
        return new CursorSample(x + dx, y, true);
    }

    private static Snowball Settle(SnowSimulation sim, float x, float y, float r, IReadOnlyList<WindowSnapshot>? windows = null)
    {
        var ball = sim.AddSnowball(x, y, r);
        Run(sim, 120, windows ?? NoWindows);
        Assert.Equal(SnowballState.Resting, ball.State);
        return ball;
    }

    [Fact]
    public void Rubbing_back_and_forth_over_piled_snow_rolls_it_into_a_snowball()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 8f);
        float before = pile.TotalArea();

        for (int i = 0; i < 90; i++)
        {
            sim.Update(Dt, windows, Rub(i, 800, 296));
        }

        var ball = Assert.Single(sim.Snowballs);
        Assert.Equal(SnowballState.Resting, ball.State);
        Assert.Same(pile, ball.Pile);
        Assert.InRange(ball.X, 780f, 820f);
        Assert.True(ball.R > 6f, $"계속 문지르면 커져야 한다 (R={ball.R})");
        Assert.True(before - pile.TotalArea() >= ball.Area - 200f, "눈덩이는 쌓인 눈을 덜어 만든다");
        // 쌓인 눈 위에 살짝 파묻혀 앉는다.
        int c = pile.ColumnAt(ball.X);
        Assert.InRange(ball.Y + ball.R, pile.BaseY - pile.H[c] - 1f, pile.BaseY + 0.5f);
    }

    [Fact]
    public void A_fast_sweep_scatters_snow_without_making_a_snowball()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        Array.Fill(sim.FindWindowPile(1)!.H, 8f);

        for (int i = 0; i < 20; i++)
        {
            sim.Update(Dt, windows, new CursorSample(600 + i * 15, 296, true)); // 초당 900px로 한 방향
        }

        Assert.Empty(sim.Snowballs);
        Assert.Contains(sim.Particles.ToArray(), p => p.Debris);
    }

    [Fact]
    public void Rubbing_where_there_is_too_little_snow_makes_nothing()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        Array.Fill(sim.FindWindowPile(1)!.H, 0.5f);

        for (int i = 0; i < 90; i++)
        {
            sim.Update(Dt, windows, Rub(i, 800, 298));
        }

        Assert.Empty(sim.Snowballs);
    }

    [Fact]
    public void A_dropped_snowball_falls_and_rests_on_the_ground()
    {
        var sim = NewSim();
        var ball = Settle(sim, 500, 400, 10);

        Assert.Same(sim.GetGround(0), ball.Pile);
        Assert.InRange(ball.Y + ball.R, GroundY - 1f, GroundY + 0.5f);
        Assert.Equal(SnowballRole.Plain, ball.Role);
    }

    [Fact]
    public void Dropping_a_snowball_onto_another_builds_a_snowman()
    {
        var sim = NewSim();
        var body = Settle(sim, 500, 900, 14);
        var head = Settle(sim, 504, 600, 9);

        Assert.Same(body, head.Below);
        Assert.Equal(SnowballRole.Body, body.Role);
        Assert.Equal(SnowballRole.Head, head.Role);
        Assert.InRange(head.X, body.X - 1f, body.X + 1f); // 가운데로 자리 잡는다
        Assert.InRange(head.Y, body.Y - (body.R + head.R), body.Y - body.R);

        // 몸통을 들어 올리면 머리는 떨어진다.
        Assert.True(sim.TryGrab(body.X, body.Y + body.R * 0.5f));
        sim.Update(Dt, NoWindows, new CursorSample(body.X, body.Y + body.R * 0.5f, true));
        Assert.Equal(SnowballState.Falling, head.State);
        Assert.Equal(SnowballRole.Plain, body.Role);
    }

    [Fact]
    public void A_snowball_landing_off_center_rolls_off_the_one_below()
    {
        var sim = NewSim();
        var body = Settle(sim, 500, 900, 14);
        var other = Settle(sim, 500 + 14 * 0.95f, 600, 9);

        Assert.Null(other.Below);
        Assert.Same(sim.GetGround(0), other.Pile);
        Assert.True(MathF.Abs(other.X - body.X) >= (body.R + other.R) * 0.85f, "나란히 놓이고 겹치지 않는다");
    }

    [Fact]
    public void A_snowball_on_a_window_moves_with_it_and_falls_when_the_window_closes()
    {
        var sim = NewSim();
        var w = new RectI(400, 300, 1200, 800);
        var windows = new[] { new WindowSnapshot(1, w, true) };
        sim.Update(Dt, windows, NoCursor);
        var ball = Settle(sim, 800, 200, 8, windows);
        Assert.Same(sim.FindWindowPile(1), ball.Pile);
        Assert.InRange(ball.Y + ball.R, 299f, 300.5f);

        for (int i = 1; i <= 30; i++)
        {
            var moved = w with { Left = w.Left + i, Right = w.Right + i };
            sim.Update(Dt, new[] { new WindowSnapshot(1, moved, true) }, NoCursor);
        }

        Assert.Equal(SnowballState.Resting, ball.State);
        Assert.InRange(ball.X, 829f, 831f);

        Run(sim, 120, NoWindows); // 창을 닫는다
        Assert.Equal(SnowballState.Resting, ball.State);
        Assert.Same(sim.GetGround(0), ball.Pile);
    }

    [Fact]
    public void A_snowball_behind_a_higher_window_is_hidden_and_cannot_be_grabbed()
    {
        var sim = NewSim();
        var lower = new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true);
        sim.Update(Dt, new[] { lower }, NoCursor);
        var ball = Settle(sim, 800, 200, 8, new[] { lower });
        Assert.NotNull(sim.SnowballAt(ball.X, ball.Y));

        var covered = new[] { new WindowSnapshot(2, new RectI(700, 250, 900, 600), true), lower };
        sim.Update(Dt, covered, NoCursor);

        Assert.False(ball.Visible);
        Assert.Null(sim.SnowballAt(ball.X, ball.Y));
        Assert.False(sim.TryGrab(ball.X, ball.Y));
    }

    [Fact]
    public void A_snowball_rising_into_a_window_in_front_is_hidden_even_if_its_pile_is_not()
    {
        var sim = NewSim();
        var lower = new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true);
        sim.Update(Dt, new[] { lower }, NoCursor);
        var ball = Settle(sim, 800, 200, 20, new[] { lower });

        // 앞쪽 창의 아래 변(280)이 눈 더미 높이 띠(286~300) 위에서 끝나 쌓인 눈은 가리지 않지만, 눈덩이(260~300)는 가린다.
        var front = new WindowSnapshot(2, new RectI(700, 100, 900, 280), true);
        var windows = new[] { front, lower };
        sim.Update(Dt, windows, NoCursor);

        var pile = sim.FindWindowPile(1)!;
        Assert.True(pile.IsExposed(pile.ColumnAt(ball.X)));
        Assert.False(ball.Visible);
        Assert.Null(sim.SnowballAt(ball.X, ball.Y - ball.R * 0.5f));

        // 앞쪽 창이 비켜나면 다시 보인다.
        sim.Update(Dt, new[] { front with { Bounds = new RectI(1300, 100, 1500, 280) }, lower }, NoCursor);
        Assert.True(ball.Visible);
    }

    [Fact]
    public void A_snowman_head_covered_by_a_window_in_front_is_hidden_while_its_body_shows()
    {
        var sim = NewSim();
        var lower = new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true);
        sim.Update(Dt, new[] { lower }, NoCursor);
        var body = Settle(sim, 800, 200, 14, new[] { lower });
        var head = Settle(sim, 800, 150, 9, new[] { lower });
        Assert.Same(body, head.Below);

        // 몸통(272~300)과 팔은 비켜 가고 머리(256~274)의 윗부분만 덮는 앞쪽 창.
        var front = new WindowSnapshot(2, new RectI(790, 100, 810, 262), true);
        sim.Update(Dt, new[] { front, lower }, NoCursor);

        Assert.True(body.Visible);
        Assert.False(head.Visible);
        Assert.Null(sim.SnowballAt(head.X, head.Y));

        // 몸통의 팔이 앞쪽 창에 걸쳐도 몸통째 숨긴다.
        var beside = new WindowSnapshot(3, new RectI(800 + 18, 200, 900, 285), true);
        sim.Update(Dt, new[] { beside, lower }, NoCursor);
        Assert.False(body.Visible);
        Assert.False(head.Visible);
    }

    [Fact]
    public void A_grabbed_snowball_follows_the_cursor_and_drops_gently_when_released()
    {
        var sim = NewSim();
        var ball = Settle(sim, 500, 900, 10);
        float y0 = ball.Y;
        Assert.True(sim.TryGrab(ball.X + 3, y0));
        Assert.True(sim.IsCarrying);

        for (int i = 1; i <= 30; i++)
        {
            sim.Update(Dt, NoWindows, new CursorSample(503 + i * 10, y0 - i * 10, true));
        }

        Assert.Equal(SnowballState.Carried, ball.State);
        Assert.InRange(ball.X, 799f, 801f);

        Run(sim, 10, NoWindows, new CursorSample(803, 600, true)); // 멈춘 뒤 놓는다
        sim.ReleaseGrab();
        Assert.False(sim.IsCarrying);
        Run(sim, 120, NoWindows, new CursorSample(803, 600, true));

        Assert.Equal(SnowballState.Resting, ball.State);
        Assert.Contains(ball, sim.Snowballs);
        Assert.InRange(ball.X, 798f, 802f);
    }

    /// <summary>
    /// (x, y)에 있는 반지름 r 눈덩이를 들고 있다가 그 자리에서 가만히 놓고 5프레임 돌린다.
    /// 짧게 보는 까닭: 면을 뚫고 떨어지면 화면 아래까지 갔다가 바닥으로 되돌려지므로, 오래 돌리면 구분되지 않는다.
    /// </summary>
    private static Snowball ReleaseAt(SnowSimulation sim, float x, float y, float r, IReadOnlyList<WindowSnapshot> windows)
    {
        var ball = sim.AddSnowball(x, y, r);
        Assert.True(sim.TryGrab(x, y));
        Run(sim, 10, windows, new CursorSample(x, y, true));
        sim.ReleaseGrab();
        Run(sim, 5, windows, new CursorSample(x, y, true));
        return ball;
    }

    [Fact]
    public void Letting_go_of_a_snowball_overlapping_a_window_edge_sets_it_on_the_edge()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);

        var ball = ReleaseAt(sim, 800, 290, 20, windows); // 아랫부분이 창 윗변(300) 아래로 들어가 있다

        Assert.Equal(SnowballState.Resting, ball.State);
        Assert.Same(sim.FindWindowPile(1), ball.Pile);
        Assert.InRange(ball.Y, 279f, 281f);
    }

    [Fact]
    public void Letting_go_of_a_snowball_sunk_in_ground_snow_or_over_the_taskbar_sets_it_on_the_ground()
    {
        var sim = NewSim();
        Array.Fill(sim.GetGround(0).H, 10f);

        var sunk = ReleaseAt(sim, 500, GroundY - 4f, 10, NoWindows);
        var low = ReleaseAt(sim, 900, GroundY + 30f, 10, NoWindows); // 작업 표시줄 위

        foreach (var ball in new[] { sunk, low })
        {
            Assert.Equal(SnowballState.Resting, ball.State);
            Assert.Same(sim.GetGround(0), ball.Pile);
            float surface = GroundY - 10f; // 쌓인 눈 표면에 반지름의 12%만큼 파묻혀 앉는다
            Assert.InRange(ball.Y + ball.R, surface - 0.5f, surface + ball.R * 0.12f + 0.1f);
        }
    }

    [Fact]
    public void Letting_go_of_a_head_already_overlapping_the_body_builds_a_snowman()
    {
        var sim = NewSim();
        var body = Settle(sim, 500, 900, 14);

        var head = ReleaseAt(sim, 502, body.Y - body.R * 0.8f, 9, NoWindows); // 몸통에 겹친 채 놓는다

        Assert.Same(body, head.Below);
        Assert.Equal(SnowballRole.Head, head.Role);
        Assert.InRange(head.Y, body.Y - (body.R + head.R), body.Y - body.R);
    }

    [Fact]
    public void Throwing_a_snowball_hard_at_the_ground_smashes_it()
    {
        var sim = NewSim();
        var ball = Settle(sim, 500, 900, 10);
        Assert.True(sim.TryGrab(ball.X, ball.Y));
        sim.Update(Dt, NoWindows, new CursorSample(500, 800, true));
        for (int i = 1; i <= 5; i++)
        {
            sim.Update(Dt, NoWindows, new CursorSample(500, 800 + i * 40, true)); // 초당 2400px로 내리꽂기
        }

        int before = sim.ParticleCount;
        sim.ReleaseGrab();
        Run(sim, 10, NoWindows);

        Assert.Empty(sim.Snowballs);
        Assert.True(sim.ParticleCount > before + 20, "부서진 눈이 파편으로 흩어져야 한다");
    }

    [Fact]
    public void Carrying_a_snowball_through_piled_snow_makes_it_bigger()
    {
        var sim = NewSim();
        var ground = sim.GetGround(0);
        var ball = Settle(sim, 300, 900, 6);
        Array.Fill(ground.H, 10f);
        float before = ground.TotalArea();
        float r0 = ball.R;

        Assert.True(sim.TryGrab(ball.X, ball.Y));
        for (int i = 0; i < 60; i++)
        {
            sim.Update(Dt, NoWindows, new CursorSample(300 + i * 5, GroundY - 10f, true)); // 눈 속을 굴린다
        }

        Assert.True(ball.R > r0 + 3f, $"굴리면 커져야 한다 ({r0} → {ball.R})");
        Assert.True(ground.TotalArea() < before - (ball.Area - MathF.PI * r0 * r0) * 0.9f);
    }

    [Fact]
    public void Clearing_snow_removes_snowballs()
    {
        var sim = NewSim();
        Settle(sim, 500, 900, 10);

        sim.ClearPiles();

        Assert.Empty(sim.Snowballs);
    }

    [Fact]
    public void With_mouse_interaction_off_snowballs_can_be_neither_made_nor_grabbed()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        Array.Fill(sim.FindWindowPile(1)!.H, 8f);
        var ball = Settle(sim, 1000, 900, 10, windows);
        sim.MouseInteraction = false;

        for (int i = 0; i < 90; i++)
        {
            sim.Update(Dt, windows, Rub(i, 800, 296));
        }

        Assert.Single(sim.Snowballs);
        Assert.Null(sim.SnowballAt(ball.X, ball.Y));
        Assert.False(sim.TryGrab(ball.X, ball.Y));
    }

    [Fact]
    public void Turning_mouse_interaction_off_drops_a_carried_snowball()
    {
        var sim = NewSim();
        var ball = Settle(sim, 500, 900, 10);
        Assert.True(sim.TryGrab(ball.X, ball.Y));

        sim.MouseInteraction = false;

        Assert.False(sim.IsCarrying);
        Assert.Equal(SnowballState.Falling, ball.State);
    }
}
