using System.Diagnostics;

namespace SnowWindow.App;

/// <summary>
/// 환경 변수 SNOWWINDOW_PROFILE=1 일 때만 단계별 평균 소요 시간을 10초마다 로그에 남긴다.
/// </summary>
internal sealed class FrameProfiler
{
    public const int Scan = 0;
    public const int Simulate = 1;
    public const int Prepare = 2;
    public const int Piles = 3;
    public const int Sprites = 4;
    public const int EndDraw = 5;
    public const int Present = 6;

    private static readonly string[] StageNames =
        { "창 추적", "시뮬레이션", "그리기 준비", "더미", "눈송이", "EndDraw", "Present" };

    private readonly long[] _ticks = new long[StageNames.Length];
    private int _frames;
    private long _windowStart = Stopwatch.GetTimestamp();

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SNOWWINDOW_PROFILE") == "1";

    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>start 이후 흐른 시간을 stage에 더하고, 다음 구간의 시작 시각을 돌려준다.</summary>
    public long Add(int stage, long start)
    {
        if (!Enabled)
        {
            return 0;
        }

        long now = Stopwatch.GetTimestamp();
        _ticks[stage] += now - start;
        return now;
    }

    public void EndFrame(int particleCount, int windowCount)
    {
        if (!Enabled)
        {
            return;
        }

        _frames++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < 10)
        {
            return;
        }

        var parts = StageNames.Select((name, i) => $"{name} {_ticks[i] * 1000.0 / Stopwatch.Frequency / _frames:F2}");
        Log.Info($"[프로파일] {_frames / elapsed:F1}fps, 입자 {particleCount}개, 창 {windowCount}개 | ms/프레임: {string.Join(", ", parts)}");
        Array.Clear(_ticks);
        _frames = 0;
        _windowStart = now;
    }
}
