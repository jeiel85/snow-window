using System.Collections.Concurrent;
using System.Diagnostics;
using SnowWindow.Platform;
using SnowWindow.Rendering;
using SnowWindow.Simulation;

namespace SnowWindow.App;

/// <summary>
/// 전용 스레드에서 창 추적 → 시뮬레이션 → 렌더링을 초당 30회(부드러운 움직임 설정 시 60회) 돈다.
/// 오버레이 창도 이 스레드가 만들고 메시지를 처리한다. UI 스레드와는 명령 큐로만 주고받는다.
/// </summary>
internal sealed class SnowEngine : IDisposable
{
    private const double MonitorPollSeconds = 1.0;
    private const double ForcedWindowScanSeconds = 1.0; // 놓친 WinEvent에 대비한 안전망
    private const double KeepOnTopSeconds = 2.0;
    private const int MaxFailuresPerMinute = 5;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action<SnowSimulation>> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly SnowIntensity _initialIntensity;
    private readonly bool _initialMouse;
    private volatile bool _enabled;
    private volatile bool _stop;
    private volatile int _framesPerSecond;

    public SnowEngine(bool enabled, SnowIntensity intensity, bool mouseInteraction, bool smoothMotion)
    {
        _enabled = enabled;
        _framesPerSecond = smoothMotion ? 60 : 30;
        _initialIntensity = intensity;
        _initialMouse = mouseInteraction;
        _thread = new Thread(Run) { IsBackground = true, Name = "SnowEngine" };
    }

    /// <summary>그래픽 오류가 반복돼 엔진이 스스로 멈췄을 때(엔진 스레드에서 호출됨).</summary>
    public event Action<string>? Faulted;

    public void Start() => _thread.Start();

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _wake.Set();
    }

    public void SetIntensity(SnowIntensity intensity) => Post(sim => sim.Intensity = intensity);

    public void SetMouseInteraction(bool enabled) => Post(sim => sim.MouseInteraction = enabled);

    public void ClearSnow() => Post(sim => sim.ClearPiles());

    /// <summary>true면 60fps, false면 30fps. 30fps는 CPU·DWM 부담이 절반이다.</summary>
    public void SetSmoothMotion(bool smooth) => _framesPerSecond = smooth ? 60 : 30;

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Log.Warn("눈 엔진 스레드가 3초 안에 끝나지 않았습니다.");
        }

        _wake.Dispose();
    }

    private void Post(Action<SnowSimulation> command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            Loop();
        }
        catch (Exception ex)
        {
            Log.Error("눈 엔진이 예기치 않게 멈췄습니다", ex);
            Faulted?.Invoke(ex.Message);
        }
    }

    private void Loop()
    {
        var sim = new SnowSimulation(Environment.TickCount) { Intensity = _initialIntensity, MouseInteraction = _initialMouse };
        var tracker = new WindowTracker();
        using var watcher = new WindowChangeWatcher();
        if (!watcher.IsActive)
        {
            Log.Warn("창 변경 감지(WinEvent)를 쓸 수 없어 매 프레임 창 목록을 훑습니다.");
        }

        using var timer = new FrameTimer();
        SnowRenderer? renderer = null;
        List<MonitorInfo> monitors = new();
        var failures = new Queue<double>();
        var profiler = new FrameProfiler();
        var clock = Stopwatch.StartNew();
        double last = 0, nextFrame = 0, nextMonitorPoll = 0, nextKeepOnTop = 0, retryAt = 0, nextForcedScan = 0;
        IReadOnlyList<WindowSnapshot> windows = Array.Empty<WindowSnapshot>();
        bool running = false;

        try
        {
            while (!_stop)
            {
                Native.PumpMessages();
                while (_commands.TryDequeue(out var command))
                {
                    command(sim);
                }

                if (!_enabled)
                {
                    if (running)
                    {
                        renderer?.Dispose();
                        renderer = null;
                        sim.Reset();
                        running = false;
                        Log.Info("눈 내리기 꺼짐");
                    }

                    _wake.WaitOne(500);
                    continue;
                }

                double now = clock.Elapsed.TotalSeconds;
                if (!running)
                {
                    running = true;
                    monitors = Monitors.GetAll();
                    sim.SetMonitors(monitors, prefill: false);
                    sim.Reset();
                    sim.Prefill();
                    last = nextFrame = now;
                    nextMonitorPoll = now + MonitorPollSeconds;
                    Log.Info($"눈 내리기 켜짐 (모니터 {monitors.Count}대)");
                }

                if (now >= nextMonitorPoll)
                {
                    nextMonitorPoll = now + MonitorPollSeconds;
                    var current = Monitors.GetAll();
                    if (sim.SetMonitors(current))
                    {
                        monitors = current;
                        Log.Info($"모니터 구성 변경 감지 (모니터 {monitors.Count}대)");
                        renderer?.Dispose();
                        renderer = null; // 아래에서 새 구성으로 다시 만든다
                    }
                }

                if (renderer == null && now >= retryAt)
                {
                    renderer = TryCreateRenderer(monitors, failures, now, ref retryAt);
                    if (renderer == null && failures.Count >= MaxFailuresPerMinute)
                    {
                        StopAfterRepeatedFailures();
                        continue;
                    }
                }

                long t0 = FrameProfiler.Now();
                // 창이 그대로면 지난 목록을 다시 쓴다(창마다 DWM에 묻는 비용을 아낀다).
                if (watcher.ConsumeChanged() || !watcher.IsActive || now >= nextForcedScan)
                {
                    nextForcedScan = now + ForcedWindowScanSeconds;
                    windows = tracker.Scan();
                    int fullscreen = tracker.FindFullscreenMonitor(monitors);
                    for (int i = 0; i < sim.MonitorCount; i++)
                    {
                        sim.SetSuspended(i, i == fullscreen);
                    }
                }

                var cursor = Native.GetCursorPos(out var pt)
                    ? new CursorSample(pt.X, pt.Y, true)
                    : new CursorSample(0, 0, false); // 보안 데스크톱(UAC) 등에서는 커서를 읽을 수 없다

                t0 = profiler.Add(FrameProfiler.Scan, t0);
                sim.Update((float)(now - last), windows, cursor);
                last = now;
                profiler.Add(FrameProfiler.Simulate, t0);

                if (renderer != null)
                {
                    try
                    {
                        renderer.Render(sim, profiler);
                        if (now >= nextKeepOnTop)
                        {
                            nextKeepOnTop = now + KeepOnTopSeconds;
                            renderer.KeepOnTop();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("그리기 실패. 그래픽 장치를 다시 만듭니다", ex);
                        renderer.Dispose();
                        renderer = null;
                        RecordFailure(failures, now);
                        retryAt = now + 1.0;
                        if (failures.Count >= MaxFailuresPerMinute)
                        {
                            StopAfterRepeatedFailures();
                            continue;
                        }
                    }
                }

                profiler.EndFrame(sim.ParticleCount, windows.Count);
                nextFrame += 1.0 / _framesPerSecond;
                double after = clock.Elapsed.TotalSeconds;
                if (nextFrame < after - 0.1)
                {
                    nextFrame = after; // 크게 밀렸으면(절전 복귀 등) 따라잡지 않고 다시 맞춘다
                }

                timer.Wait(TimeSpan.FromSeconds(nextFrame - after));
            }
        }
        finally
        {
            renderer?.Dispose();
        }
    }

    private static SnowRenderer? TryCreateRenderer(List<MonitorInfo> monitors, Queue<double> failures, double now, ref double retryAt)
    {
        SnowRenderer? created = null;
        try
        {
            created = new SnowRenderer();
            created.SetMonitors(monitors);
            TrimFailures(failures, now);
            Log.Info(failures.Count > 0
                ? $"그래픽 장치를 다시 만들었습니다 (모니터 {monitors.Count}대, 최근 1분 실패 {failures.Count}회)"
                : $"그래픽 장치 준비됨 (모니터 {monitors.Count}대)");
            return created;
        }
        catch (Exception ex)
        {
            Log.Error("그래픽 장치를 만들지 못했습니다", ex);
            created?.Dispose();
            RecordFailure(failures, now);
            retryAt = now + 2.0;
            return null;
        }
    }

    private static void RecordFailure(Queue<double> failures, double now)
    {
        failures.Enqueue(now);
        TrimFailures(failures, now);
    }

    private static void TrimFailures(Queue<double> failures, double now)
    {
        while (failures.Count > 0 && failures.Peek() < now - 60)
        {
            failures.Dequeue();
        }
    }

    private void StopAfterRepeatedFailures()
    {
        _enabled = false;
        string message = $"그래픽 오류가 1분 안에 {MaxFailuresPerMinute}번 반복되어 눈 내리기를 멈췄습니다.";
        Log.Error(message);
        Faulted?.Invoke(message);
    }
}
