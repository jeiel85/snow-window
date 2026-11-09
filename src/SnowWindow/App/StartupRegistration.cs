using Microsoft.Win32;

namespace SnowWindow.App;

/// <summary>HKCU\...\Run 에 등록해 Windows 로그인 시 자동 실행한다(관리자 권한 불필요).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnowWindow";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value
            && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// 시작 시 실행이 켜져 있는데 등록된 경로가 지금 실행 중인 exe와 다르면 지금 exe로 바꾼다.
    /// 새 버전을 다른 폴더에 내려받아 쓰면 로그인 때 옛 버전이 켜지거나 아무것도 켜지지 않는 것을 막는다.
    /// 마지막으로 실행한 exe가 로그인 때 켜진다.
    /// </summary>
    /// <returns>바꿨으면 이전에 등록돼 있던 값, 바꾸지 않았으면 null</returns>
    public static string? SyncToCurrentPath()
    {
        if (Environment.ProcessPath is null)
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string registered || !NeedsPathUpdate(registered, Command))
        {
            return null;
        }

        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return registered;
    }

    internal static bool NeedsPathUpdate(string? registered, string current) =>
        registered is not null
        && !string.Equals(registered.Trim(), current, StringComparison.OrdinalIgnoreCase);
}
