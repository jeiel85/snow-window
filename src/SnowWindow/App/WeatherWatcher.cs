using System.Text.Json;
using SnowWindow.Simulation;

namespace SnowWindow.App;

/// <summary>마지막으로 확인한 바깥 날씨. <paramref name="Snow"/>가 null이면 눈이 오지 않는다.</summary>
internal sealed record WeatherState(string Place, int Code, SnowIntensity? Snow, DateTime CheckedAt, string? Error);

/// <summary>
/// 15분마다 바깥 날씨를 확인한다(위치는 하루에 한 번 다시 찾는다). UI 스레드에서 만들고 쓴다.
/// 확인에 실패하면 마지막 결과를 유지하고 오류만 표시한다.
/// </summary>
internal sealed class WeatherWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LocationMaxAge = TimeSpan.FromHours(24);

    private readonly System.Windows.Forms.Timer _timer;
    private GeoLocation? _location;
    private DateTime _locatedAt;
    private bool _busy;

    public WeatherWatcher()
    {
        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>확인할 때마다(결과가 같아도) UI 스레드에서 알린다.</summary>
    public event Action<WeatherState>? Changed;

    public WeatherState? Current { get; private set; }

    public bool Running => _timer.Enabled;

    public void Start()
    {
        if (Running)
        {
            return;
        }

        _timer.Start();
        Refresh();
    }

    public void Stop()
    {
        _timer.Stop();
        Current = null;
    }

    public async void Refresh()
    {
        if (_busy || !Running)
        {
            return;
        }

        _busy = true;
        WeatherState state;
        try
        {
            // 위치 서비스 두 곳(각 8초)을 모두 기다리고도 날씨를 받을 시간이 남게 잡는다.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (_location is null || DateTime.UtcNow - _locatedAt > LocationMaxAge)
            {
                _location = await Weather.LocateAsync(cts.Token);
                _locatedAt = DateTime.UtcNow;
                Log.Info($"날씨 연동 위치: {_location.Name}");
            }

            var now = await Weather.FetchCurrentAsync(_location, cts.Token);
            state = new WeatherState(_location.Name, now.Code, Weather.SnowIntensityFor(now.Code, now.SnowfallCm), DateTime.Now, null);
            if (Current is null || Current.Snow != state.Snow || Current.Error != null)
            {
                Log.Info($"날씨: {state.Place} {Weather.Describe(now.Code)} (코드 {now.Code}, 적설 {now.SnowfallCm}cm) → 눈 {state.Snow?.ToString() ?? "없음"}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            Log.Warn("날씨를 확인하지 못했습니다", ex);
            state = Current is null
                ? new WeatherState(_location?.Name ?? "", -1, null, DateTime.Now, ex.Message)
                : Current with { Error = ex.Message };
        }
        finally
        {
            _busy = false;
        }

        if (!Running)
        {
            return; // 확인하는 사이에 꺼졌다
        }

        Current = state;
        Changed?.Invoke(state);
    }

    public void Dispose() => _timer.Dispose();
}
