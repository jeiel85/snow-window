using SnowWindow.App;
using SnowWindow.Simulation;

namespace SnowWindow.Tests;

public class WeatherTests
{
    [Theory]
    [InlineData(71, 0.0, SnowIntensity.Light)]   // 약한 눈
    [InlineData(77, 0.0, SnowIntensity.Light)]   // 싸락눈
    [InlineData(85, 0.0, SnowIntensity.Light)]   // 약한 눈 소나기
    [InlineData(73, 0.2, SnowIntensity.Normal)]  // 보통 눈
    [InlineData(75, 0.5, SnowIntensity.Heavy)]   // 강한 눈
    [InlineData(86, 0.0, SnowIntensity.Heavy)]   // 강한 눈 소나기
    [InlineData(71, 1.2, SnowIntensity.Heavy)]   // 코드는 약해도 적설이 많으면 펑펑
    [InlineData(67, 0.1, SnowIntensity.Light)]   // 어는 비인데 적설이 잡히면 조금
    public void Snowy_weather_maps_to_an_intensity(int code, double snowfallCm, SnowIntensity expected)
    {
        Assert.Equal(expected, Weather.SnowIntensityFor(code, snowfallCm));
    }

    [Theory]
    [InlineData(0)]   // 맑음
    [InlineData(3)]   // 흐림
    [InlineData(61)]  // 비
    [InlineData(95)]  // 뇌우
    public void Weather_without_snow_means_no_snow(int code)
    {
        Assert.Null(Weather.SnowIntensityFor(code, 0));
    }

    [Theory]
    [InlineData(0, "맑음")]
    [InlineData(73, "눈")]
    [InlineData(86, "눈 소나기")]
    [InlineData(61, "비")]
    [InlineData(42, "날씨 코드 42")]
    public void Describes_weather_codes_in_korean(int code, string expected)
    {
        Assert.Equal(expected, Weather.Describe(code));
    }

    [Fact]
    public void Reads_location_from_ipapi()
    {
        const string json = """
            { "ip": "203.0.113.7", "city": "Seoul", "region": "Seoul", "country": "KR",
              "latitude": 37.566309, "longitude": 126.977207, "timezone": "Asia/Seoul" }
            """;
        var location = Weather.ParseLocation(json);
        Assert.Equal(new GeoLocation(37.566309, 126.977207, "Seoul"), location);
    }

    [Fact]
    public void Reads_location_from_ipwhois()
    {
        const string json = """
            { "ip": "203.0.113.7", "success": true, "city": "Busan", "region": "Busan",
              "latitude": 35.1796, "longitude": 129.0756, "timezone": { "id": "Asia/Seoul" } }
            """;
        Assert.Equal(new GeoLocation(35.1796, 129.0756, "Busan"), Weather.ParseLocation(json));
    }

    [Theory]
    [InlineData("""{ "error": true, "reason": "RateLimited" }""")]
    [InlineData("""{ "success": false, "message": "Invalid IP address" }""")]
    [InlineData("""{ "city": "Nowhere" }""")]
    [InlineData("""{ "city": "Bad", "latitude": 123.0, "longitude": 10.0 }""")]
    [InlineData("""[]""")]
    public void Error_or_incomplete_location_is_rejected(string json)
    {
        Assert.Null(Weather.ParseLocation(json));
    }

    [Fact]
    public void Missing_city_falls_back_to_region_or_generic_name()
    {
        Assert.Equal("Gyeonggi-do", Weather.ParseLocation("""{ "region": "Gyeonggi-do", "latitude": 37.4, "longitude": 127.1 }""")!.Name);
        Assert.Equal("현재 위치", Weather.ParseLocation("""{ "latitude": 37.4, "longitude": 127.1 }""")!.Name);
    }

    private const string First = "https://first.example/json";
    private const string Second = "https://second.example/json";
    private const string BusanJson = """{ "city": "Busan", "latitude": 35.18, "longitude": 129.08 }""";

    [Fact]
    public async Task Stalled_provider_times_out_and_the_fallback_is_used()
    {
        var asked = new List<string>();
        Task<string> Get(string url, CancellationToken token)
        {
            asked.Add(url);
            return url == First
                ? Task.Delay(Timeout.Infinite, token).ContinueWith(_ => "", token) // 답하지 않는 서비스
                : Task.FromResult(BusanJson);
        }

        var location = await Weather.LocateAsync([First, Second], Get, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal("Busan", location.Name);
        Assert.Equal([First, Second], asked);
    }

    [Fact]
    public async Task Error_response_moves_on_to_the_fallback()
    {
        Task<string> Get(string url, CancellationToken token) =>
            Task.FromResult(url == First ? """{ "error": true, "reason": "RateLimited" }""" : BusanJson);

        var location = await Weather.LocateAsync([First, Second], Get, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Busan", location.Name);
    }

    [Fact]
    public async Task Caller_cancellation_stops_without_trying_the_fallback()
    {
        using var cts = new CancellationTokenSource();
        var asked = new List<string>();
        Task<string> Get(string url, CancellationToken token)
        {
            asked.Add(url);
            cts.Cancel();
            return Task.FromCanceled<string>(token);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Weather.LocateAsync([First, Second], Get, TimeSpan.FromSeconds(5), cts.Token));
        Assert.Equal([First], asked);
    }

    [Fact]
    public async Task All_providers_failing_reports_the_last_reason()
    {
        Task<string> Get(string url, CancellationToken token) => throw new HttpRequestException($"{url} unreachable");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => Weather.LocateAsync([First, Second], Get, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("second.example", ex.InnerException!.Message);
    }

    [Fact]
    public void Reads_current_weather_from_open_meteo()
    {
        const string json = """
            { "latitude": 37.55, "longitude": 127.0,
              "current": { "time": "2026-11-10T10:15", "interval": 900, "weather_code": 73, "snowfall": 0.35 } }
            """;
        Assert.Equal(new CurrentWeather(73, 0.35), Weather.ParseCurrentWeather(json));
    }

    [Fact]
    public void Missing_snowfall_counts_as_zero_and_missing_current_is_rejected()
    {
        Assert.Equal(new CurrentWeather(0, 0), Weather.ParseCurrentWeather("""{ "current": { "weather_code": 0 } }"""));
        Assert.Null(Weather.ParseCurrentWeather("""{ "error": true, "reason": "Latitude must be in range" }"""));
    }
}
