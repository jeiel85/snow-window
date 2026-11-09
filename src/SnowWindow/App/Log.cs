using System.Diagnostics;

namespace SnowWindow.App;

/// <summary>%LOCALAPPDATA%\SnowWindow\snowwindow.log 에 남기는 간단한 로그. 1MB를 넘으면 .old로 돌린다.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowWindow");

    public static string FilePath { get; } = Path.Combine(Directory, "snowwindow.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", ex == null ? message : $"{message}: {ex}");

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line);
            }
            catch (IOException ioEx)
            {
                // 로그를 못 쓴다고 앱을 멈추지는 않는다. 디버거 출력으로만 남긴다.
                Debug.WriteLine($"로그 기록 실패: {ioEx.Message} / {line}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Debug.WriteLine($"로그 기록 실패: {uaEx.Message} / {line}");
            }
        }
    }
}
