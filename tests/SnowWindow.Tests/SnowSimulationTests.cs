using SnowWindow.Simulation;

namespace SnowWindow.Tests;

public class SnowSimulationTests
{
    private const float Dt = 1f / 60f;
    private static readonly MonitorInfo Monitor = new(new RectI(0, 0, 1920, 1080), new RectI(0, 0, 1920, 1032), 1f);
    private static readonly CursorSample NoCursor = new(0, 0, false);

    private static SnowSimulation NewSim()
    {
        var sim = new SnowSimulation(seed: 1234) { LandingChanceOverride = 1f };
        sim.SetMonitors(new[] { Monitor }, prefill: false);
        return sim;
    }

    private static Particle FallingFlake(float x, float y) => new()
    {
        X = x,
        Y = y,
        VY = 120f,
        TargetVY = 120f,
        Radius = 2f,
        Alpha = 1f,
        Depth = 1f,
        Volume = 200f,
        Monitor = 0,
    };

    private static float Sum(float[] h) => h.Sum();

    [Fact]
    public void Flake_crossing_an_exposed_window_edge_lands_on_it()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);

        sim.InjectParticle(FallingFlake(800, 298));
        for (int i = 0; i < 10; i++)
        {
            sim.Update(Dt, windows, NoCursor);
        }

        var pile = sim.FindWindowPile(1)!;
        Assert.True(Sum(pile.H) > 0f);
        int col = pile.ColumnAt(800);
        Assert.True(pile.H[col] > 0f || pile.H[col - 1] > 0f || pile.H[col + 1] > 0f);
    }

    [Fact]
    public void Edge_covered_by_a_higher_window_is_not_exposed_and_receives_no_snow()
    {
        var sim = NewSim();
        var windows = new[]
        {
            new WindowSnapshot(2, new RectI(700, 250, 900, 600), true), // 위(앞)
            new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true), // 아래(뒤)
        };
        sim.Update(Dt, windows, NoCursor);

        var lower = sim.FindWindowPile(1)!;
        Assert.False(lower.IsExposed(lower.ColumnAt(800)));
        Assert.True(lower.IsExposed(lower.ColumnAt(500)));
        Assert.True(lower.IsExposed(lower.ColumnAt(1000)));

        sim.InjectParticle(FallingFlake(800, 298));
        for (int i = 0; i < 10; i++)
        {
            sim.Update(Dt, windows, NoCursor);
        }

        Assert.Equal(0f, Sum(lower.H));
    }

    [Fact]
    public void Pile_height_never_exceeds_its_maximum()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        var pile = sim.FindWindowPile(1)!;

        for (int i = 0; i < 400; i++)
        {
            sim.InjectParticle(FallingFlake(800 + (i % 20), 299));
            sim.Update(Dt, windows, NoCursor);
        }

        Assert.All(pile.H, h => Assert.True(h <= pile.MaxH + 1e-3f));
        Assert.True(pile.H.Max() > pile.MaxH * 0.5f);
    }

    [Fact]
    public void Shrinking_a_window_spills_the_snow_beyond_its_new_edge()
    {
        var sim = NewSim();
        var big = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, big, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 6f);
        float before = pile.TotalArea();

        int particlesBefore = sim.ParticleCount;
        var small = new[] { new WindowSnapshot(1, new RectI(400, 300, 800, 800), true) };
        sim.Update(Dt, small, NoCursor);

        Assert.True(pile.TotalArea() < before * 0.6f);
        Assert.True(sim.ParticleCount > particlesBefore + 50, "넘친 눈이 파편으로 떨어져야 한다");
        // 왼쪽 끝은 그대로 있으므로 남은 칸의 눈은 유지된다.
        Assert.True(pile.H[pile.Columns / 2] > 5f);
    }

    [Fact]
    public void Moving_a_window_slowly_carries_its_snow_along()
    {
        var sim = NewSim();
        var w = new RectI(400, 300, 1200, 800);
        sim.Update(Dt, new[] { new WindowSnapshot(1, w, true) }, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 6f);
        int middle = pile.Columns / 2;

        // 프레임당 1px(초당 60px) 이동은 털리지 않는다.
        for (int i = 1; i <= 30; i++)
        {
            var moved = w with { Left = w.Left + i, Right = w.Right + i };
            sim.Update(Dt, new[] { new WindowSnapshot(1, moved, true) }, NoCursor);
        }

        Assert.Equal(434f, pile.Left, 0.01f); // 400 + 30 + 여백 4
        Assert.True(pile.H[middle] > 5.5f);
    }

    [Fact]
    public void Shaking_a_window_fast_throws_its_snow_off()
    {
        var sim = NewSim();
        var w = new RectI(400, 300, 1200, 800);
        sim.Update(Dt, new[] { new WindowSnapshot(1, w, true) }, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 6f);
        float before = pile.TotalArea();

        for (int i = 1; i <= 20; i++)
        {
            int dx = (i % 2 == 0 ? 1 : -1) * 40; // 초당 2400px로 흔들기
            var moved = w with { Left = w.Left + dx, Right = w.Right + dx };
            sim.Update(Dt, new[] { new WindowSnapshot(1, moved, true) }, NoCursor);
        }

        Assert.True(pile.TotalArea() < before * 0.5f);
    }

    [Fact]
    public void Closing_a_window_drops_its_snow_as_debris()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        Array.Fill(sim.FindWindowPile(1)!.H, 6f);

        int before = sim.ParticleCount;
        sim.Update(Dt, Array.Empty<WindowSnapshot>(), NoCursor);

        Assert.Null(sim.FindWindowPile(1));
        Assert.True(sim.ParticleCount > before + 100);
    }

    [Fact]
    public void Maximized_window_touching_the_screen_top_does_not_collect_snow()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(0, 0, 1920, 1032), true) };
        sim.Update(Dt, windows, NoCursor);

        Assert.False(sim.FindWindowPile(1)!.Landable);
    }

    [Fact]
    public void Sweeping_the_cursor_through_a_pile_kicks_snow_up()
    {
        var sim = NewSim();
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 8f);
        float before = pile.TotalArea();

        for (int i = 0; i < 20; i++)
        {
            sim.Update(Dt, windows, new CursorSample(600 + i * 15, 296, true)); // 초당 900px
        }

        // 창 양 끝에서 흘러내리는 눈(x≈404, 1196)과 구분하려고 커서가 지나간 구간만 본다.
        Assert.True(pile.H[pile.ColumnAt(750)] < 6f, "커서가 지나간 자리의 눈이 줄어야 한다");
        Assert.True(pile.H[pile.ColumnAt(1100)] > 7.9f, "커서가 닿지 않은 자리는 그대로여야 한다");
        Assert.Contains(sim.Particles.ToArray(), p => p.Debris && p.X > 550 && p.X < 1000 && p.Y < 300);
    }

    [Fact]
    public void Disabling_mouse_interaction_leaves_piles_alone()
    {
        var sim = NewSim();
        sim.MouseInteraction = false;
        var windows = new[] { new WindowSnapshot(1, new RectI(400, 300, 1200, 800), true) };
        sim.Update(Dt, windows, NoCursor);
        var pile = sim.FindWindowPile(1)!;
        Array.Fill(pile.H, 8f);

        for (int i = 0; i < 20; i++)
        {
            sim.Update(Dt, windows, new CursorSample(600 + i * 15, 296, true));
        }

        Assert.True(pile.H[pile.ColumnAt(750)] > 7.9f);
        Assert.DoesNotContain(sim.Particles.ToArray(), p => p.Debris && p.X > 550 && p.X < 1000);
    }

    [Fact]
    public void Snow_falls_continuously_and_settles_on_the_ground()
    {
        var sim = new SnowSimulation(seed: 7) { Intensity = SnowIntensity.Heavy };
        sim.SetMonitors(new[] { Monitor });
        Assert.True(sim.ParticleCount > 500);

        for (int i = 0; i < 60 * 30; i++)
        {
            sim.Update(Dt, Array.Empty<WindowSnapshot>(), NoCursor);
        }

        Assert.True(sim.ParticleCount > 500, "눈이 계속 내려야 한다");
        var ground = sim.GetGround(0);
        Assert.Equal(1032f, ground.BaseY);
        Assert.True(ground.TotalArea() > 0f);
    }

    [Fact]
    public void Suspended_monitor_spawns_no_new_flakes()
    {
        var sim = new SnowSimulation(seed: 3);
        sim.SetMonitors(new[] { Monitor }, prefill: false);
        sim.SetSuspended(0, true);
        for (int i = 0; i < 120; i++)
        {
            sim.Update(Dt, Array.Empty<WindowSnapshot>(), NoCursor);
        }

        Assert.Equal(0, sim.ParticleCount);
    }
}
