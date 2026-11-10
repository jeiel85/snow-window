using System.Text.Json;
using System.Text.Json.Serialization;
using SnowWindow.Simulation;

namespace SnowWindow.App;

/// <summary>%APPDATA%\SnowWindow\settings.json 에 저장되는 사용자 설정.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowWindow", "settings.json");

    public bool Enabled { get; set; } = true;

    public SnowIntensity Intensity { get; set; } = SnowIntensity.Normal;

    public bool MouseInteraction { get; set; } = true;

    /// <summary>true면 60fps로 더 부드럽지만 CPU·GPU를 두 배 쓴다. 기본은 30fps.</summary>
    public bool SmoothMotion { get; set; }

    /// <summary>true면 하루에 한 번 GitHub에서 새 버전을 확인하고 알린다(설치는 사용자가 누를 때만).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>true면 밖의 날씨를 15분마다 확인해, 눈이 올 때만 그 양만큼 내린다(눈 내리기·눈의 양 설정보다 우선).</summary>
    public bool FollowWeather { get; set; }

    /// <summary>설정 파일이 없거나 읽을 수 없으면 기본값을 쓴다(읽기 실패는 로그에 남긴다).</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"설정 파일을 읽지 못해 기본값을 씁니다 ({FilePath})", ex);
            return new AppSettings();
        }
    }

    /// <summary>임시 파일에 쓴 뒤 바꿔치기해서, 저장 도중 꺼져도 파일이 깨지지 않게 한다.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"설정을 저장하지 못했습니다 ({FilePath})", ex);
            throw;
        }
    }
}
