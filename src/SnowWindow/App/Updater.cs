using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SnowWindow.App;

/// <summary>GitHub 최신 릴리스에서 이 앱이 쓰는 정보.</summary>
/// <param name="Sha256">GitHub가 자산마다 주는 SHA-256(소문자 hex). 없으면 null이고, 그때는 설치하지 않는다.</param>
internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// GitHub 릴리스로 자기 자신을 업데이트한다.
/// 확인: releases/latest API를 읽어 지금보다 높은 버전이면 알린다.
/// 설치(사용자가 누를 때만): exe 옆에 내려받기 → 크기·SHA-256·파일 버전 확인 →
/// 실행 중인 exe를 .old로 이름 바꾸고 새 exe를 제자리에 놓기 → 새 exe를 --after-update로 실행 → 종료.
/// 결정 배경은 DECISIONS.md 참고.
/// </summary>
internal static class Updater
{
    public const string Repository = "jeiel85/snow-window";
    public const string AssetName = "SnowWindow-win-x64.exe";
    public const string AfterUpdateArg = "--after-update";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    public static Version CurrentVersion { get; } = ParseVersion(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    // 시간 제한은 요청마다 CancellationToken으로 건다(확인은 짧게, 내려받기는 길게).
    private static HttpClient Http => Net.Client;

    /// <summary>"v0.1.1", "0.1.1+2af0259", "1.2" 같은 문자열을 Major.Minor.Build 버전으로 읽는다.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        int cut = s.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>releases/latest 응답을 읽는다. 초안·사전 릴리스이거나 exe 자산이 없으면 null.</summary>
    public static ReleaseInfo? ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (GetBool(root, "draft") || GetBool(root, "prerelease"))
        {
            return null;
        }

        string? tag = GetString(root, "tag_name");
        string? page = GetString(root, "html_url");
        var version = ParseVersion(tag);
        if (tag is null || page is null || version is null || !root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (GetString(asset, "name") != AssetName)
            {
                continue;
            }

            string? url = GetString(asset, "browser_download_url");
            if (url is null || !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes) || bytes <= 0)
            {
                return null;
            }

            return new ReleaseInfo(version, tag, page, url, bytes, ParseSha256Digest(GetString(asset, "digest")));
        }

        return null;
    }

    private static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>최신 릴리스를 읽는다. 릴리스가 하나도 없으면 null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseLatestRelease(json);
    }

    /// <summary>
    /// 이 실행 파일을 제자리에서 바꿀 수 있는지. 런타임 포함 단일 exe(릴리스 빌드)이고 그 폴더에 쓸 수 있어야 한다.
    /// 개발 빌드(bin 폴더의 dll 묶음)는 바꾸지 않는다.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            reason = "실행 파일 위치를 알 수 없습니다.";
            return false;
        }

        // 단일 파일로 묶인 앱에서는 어셈블리 위치가 빈 문자열이다. IL3000이 경고하는 바로 그 동작으로 판별한다.
#pragma warning disable IL3000
        bool singleFile = string.IsNullOrEmpty(typeof(Updater).Assembly.Location);
#pragma warning restore IL3000
        if (!singleFile)
        {
            reason = "개발 빌드라서 자동으로 바꾸지 않습니다.";
            return false;
        }

        string probe = exe + ".write-test";
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = $"실행 파일이 있는 폴더에 쓸 수 없습니다 ({Path.GetDirectoryName(exe)}).";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>새 exe를 <paramref name="destination"/>에 내려받고 크기·SHA-256·파일 버전을 확인한다. 실패하면 받은 파일을 지운다.</summary>
    public static async Task DownloadAsync(ReleaseInfo release, string destination, CancellationToken ct)
    {
        if (release.Sha256 is null)
        {
            throw new InvalidDataException("릴리스에 SHA-256 정보가 없어 내려받은 파일을 확인할 수 없습니다.");
        }

        try
        {
            using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            VerifyFile(destination, release.AssetSize, release.Sha256);
            VerifyProductVersion(destination, release.Version);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static void VerifyFile(string path, long expectedSize, string expectedSha256)
    {
        long size = new FileInfo(path).Length;
        if (size != expectedSize)
        {
            throw new InvalidDataException($"내려받은 파일 크기가 다릅니다 (기대 {expectedSize}, 실제 {size}).");
        }

        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("내려받은 파일의 SHA-256이 릴리스와 다릅니다.");
        }
    }

    private static void VerifyProductVersion(string path, Version expected)
    {
        var actual = ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        if (actual != expected)
        {
            throw new InvalidDataException($"내려받은 파일의 버전({actual?.ToString() ?? "없음"})이 릴리스 v{expected}와 다릅니다.");
        }
    }

    /// <summary>
    /// 실행 중인 exe를 .old로 이름 바꾸고(실행 중이어도 이름은 바꿀 수 있다) 새 파일을 제자리로 옮긴다.
    /// 옮기지 못하면 원래 exe를 되돌려 놓는다.
    /// </summary>
    public static void SwapExecutable(string exePath, string newPath)
    {
        string old = OldPath(exePath);
        File.Move(exePath, old, overwrite: true);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>지난 업데이트가 남긴 .old·.download 파일을 지운다. 지운 파일 수를 돌려준다.</summary>
    public static int CleanupLeftovers(string exePath)
    {
        int removed = 0;
        foreach (string path in new[] { OldPath(exePath), DownloadPath(exePath) })
        {
            if (File.Exists(path) && TryDelete(path))
            {
                removed++;
            }
        }

        return removed;
    }

    public static string OldPath(string exePath) => exePath + ".old";

    public static string DownloadPath(string exePath) => exePath + ".download";

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 이전 프로세스가 아직 파일을 잡고 있을 수 있다. 다음 확인 때 다시 지운다.
            return false;
        }
    }
}
