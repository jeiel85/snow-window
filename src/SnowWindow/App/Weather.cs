using System.Globalization;
using System.Text.Json;
using SnowWindow.Simulation;

namespace SnowWindow.App;

/// <summary>IP로 알아낸 대략적인 위치(도시 단위).</summary>
internal sealed record GeoLocation(double Latitude, double Longitude, string Name);

/// <summary>Open-Meteo 현재 날씨. <paramref name="Code"/>는 WMO 날씨 코드, 적설은 직전 1시간 cm.</summary>
internal sealed record CurrentWeather(int Code, double SnowfallCm);

/// <summary>
/// 밖의 날씨를 읽어 눈이 오는지 판단한다.
/// 위치: IP 위치 조회(ipapi.co, 실패하면 ipwho.is). 날씨: Open-Meteo(키 없음, CC BY 4.0).
/// 위치 조회 서비스에는 IP가, Open-Meteo에는 소수 둘째 자리로 줄인 좌표가 전달된다. 결정 배경은 DECISIONS.md.
/// </summary>
internal static class Weather
{
    private static readonly string[] LocationProviders = ["https://ipapi.co/json/", "https://ipwho.is/"];

    /// <summary>WMO 날씨 코드와 적설량으로 화면에 내릴 눈의 양을 정한다. 눈이 오지 않으면 null.</summary>
    public static SnowIntensity? SnowIntensityFor(int code, double snowfallCm)
    {
        SnowIntensity? byCode = code switch
        {
            75 or 86 => SnowIntensity.Heavy,          // 강한 눈, 강한 눈 소나기
            73 => SnowIntensity.Normal,               // 보통 눈
            71 or 77 or 85 => SnowIntensity.Light,    // 약한 눈, 싸락눈, 약한 눈 소나기
            _ => null,
        };

        if (snowfallCm >= 1.0)
        {
            return SnowIntensity.Heavy;
        }

        // 코드가 진눈깨비·비라도 적설이 잡히면 조금 내린다.
        return byCode ?? (snowfallCm > 0 ? SnowIntensity.Light : null);
    }

    /// <summary>WMO 날씨 코드를 짧은 한국어로.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "맑음",
        1 or 2 => "구름 조금",
        3 => "흐림",
        45 or 48 => "안개",
        >= 51 and <= 57 => "이슬비",
        >= 61 and <= 67 => "비",
        >= 71 and <= 77 => "눈",
        >= 80 and <= 82 => "소나기",
        85 or 86 => "눈 소나기",
        >= 95 and <= 99 => "뇌우",
        _ => $"날씨 코드 {code}",
    };

    /// <summary>ipapi.co·ipwho.is 응답에서 위치를 읽는다. 오류 응답이거나 좌표가 없으면 null.</summary>
    public static GeoLocation? ParseLocation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False))
        {
            return null;
        }

        if (!TryGetNumber(root, "latitude", out double lat) || !TryGetNumber(root, "longitude", out double lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return null;
        }

        string name = GetString(root, "city") ?? GetString(root, "region") ?? "현재 위치";
        return new GeoLocation(lat, lon, name);
    }

    /// <summary>Open-Meteo 응답의 current 블록을 읽는다.</summary>
    public static CurrentWeather? ParseCurrentWeather(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("current", out var current)
            || !current.TryGetProperty("weather_code", out var code) || !code.TryGetInt32(out int wmo))
        {
            return null;
        }

        double snowfall = TryGetNumber(current, "snowfall", out double cm) ? Math.Max(0, cm) : 0;
        return new CurrentWeather(wmo, snowfall);
    }

    public static async Task<GeoLocation> LocateAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (string url in LocationProviders)
        {
            try
            {
                string json = await Net.Client.GetStringAsync(url, ct).ConfigureAwait(false);
                if (ParseLocation(json) is { } location)
                {
                    return location;
                }

                last = new InvalidDataException($"{new Uri(url).Host}가 위치를 알려 주지 않았습니다.");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                last = ex;
            }
        }

        throw new InvalidDataException("IP로 위치를 알아내지 못했습니다.", last);
    }

    public static async Task<CurrentWeather> FetchCurrentAsync(GeoLocation location, CancellationToken ct)
    {
        // 도시 단위면 충분하므로 좌표를 소수 둘째 자리(약 1km)로 줄여 보낸다.
        string lat = Math.Round(location.Latitude, 2).ToString(CultureInfo.InvariantCulture);
        string lon = Math.Round(location.Longitude, 2).ToString(CultureInfo.InvariantCulture);
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=weather_code,snowfall";
        string json = await Net.Client.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseCurrentWeather(json) ?? throw new InvalidDataException("날씨 응답에 현재 날씨가 없습니다.");
    }

    private static bool TryGetNumber(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()
            : null;
}
