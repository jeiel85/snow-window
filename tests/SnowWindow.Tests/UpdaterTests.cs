using System.Security.Cryptography;
using SnowWindow.App;

namespace SnowWindow.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SnowWindowTests", Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("v0.1.1", "0.1.1")]
    [InlineData("0.1.1+2af0259f8e908afe6b0f34ca97654178e8aba98c", "0.1.1")]
    [InlineData("V1.2", "1.2.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData(" 2.0.0 ", "2.0.0")]
    public void Parses_tags_and_informational_versions(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Rejects_text_that_is_not_a_version(string? text)
    {
        Assert.Null(Updater.ParseVersion(text));
    }

    [Fact]
    public void Three_part_versions_compare_as_expected()
    {
        Assert.True(Updater.ParseVersion("v0.2.0") > Updater.ParseVersion("0.1.1+abc"));
        Assert.Equal(Updater.ParseVersion("v0.1.1"), Updater.ParseVersion("0.1.1.0"));
    }

    private static string ReleaseJson(
        string tag = "v0.2.0",
        string assetName = "SnowWindow-win-x64.exe",
        string digest = "\"sha256:D178437BA283D86E46A40A683CB35E8F4C1E3F81416BB5DB1AACB74B9DD9CAF7\"",
        bool draft = false,
        bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/jeiel85/snow-window/releases/tag/{{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://example.invalid/notes.txt" },
            {
              "name": "{{assetName}}",
              "size": 52551358,
              "digest": {{digest}},
              "browser_download_url": "https://github.com/jeiel85/snow-window/releases/download/{{tag}}/{{assetName}}"
            }
          ]
        }
        """;

    [Fact]
    public void Reads_version_asset_and_lowercase_digest_from_latest_release()
    {
        var release = Updater.ParseLatestRelease(ReleaseJson());

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("https://github.com/jeiel85/snow-window/releases/download/v0.2.0/SnowWindow-win-x64.exe", release.AssetUrl);
        Assert.Equal(52551358, release.AssetSize);
        Assert.Equal("d178437ba283d86e46a40a683cb35e8f4c1e3f81416bb5db1aacb74b9dd9caf7", release.Sha256);
    }

    [Fact]
    public void Release_without_the_exe_asset_is_ignored()
    {
        Assert.Null(Updater.ParseLatestRelease(ReleaseJson(assetName: "Other.exe")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Draft_or_prerelease_is_ignored(bool draft, bool prerelease)
    {
        Assert.Null(Updater.ParseLatestRelease(ReleaseJson(draft: draft, prerelease: prerelease)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"md5:abc\"")]
    [InlineData("\"sha256:1234\"")]
    [InlineData("\"sha256:zz78437ba283d86e46a40a683cb35e8f4c1e3f81416bb5db1aacb74b9dd9caf7\"")]
    public void Missing_or_malformed_digest_leaves_hash_unknown(string digest)
    {
        var release = Updater.ParseLatestRelease(ReleaseJson(digest: digest));
        Assert.NotNull(release);
        Assert.Null(release.Sha256);
    }

    [Fact]
    public void Verify_accepts_matching_size_and_hash()
    {
        string path = Write("payload.exe", "new build");
        Updater.VerifyFile(path, new FileInfo(path).Length, Sha256Of(path));
    }

    [Fact]
    public void Verify_rejects_wrong_hash_or_size()
    {
        string path = Write("payload.exe", "new build");
        long size = new FileInfo(path).Length;
        string wrong = new string('0', 64);

        Assert.Throws<InvalidDataException>(() => Updater.VerifyFile(path, size, wrong));
        Assert.Throws<InvalidDataException>(() => Updater.VerifyFile(path, size + 1, Sha256Of(path)));
    }

    [Fact]
    public void Swap_puts_new_file_in_place_and_keeps_old_one_aside()
    {
        string exe = Write("SnowWindow.exe", "old");
        string download = Write("SnowWindow.exe.download", "new");

        Updater.SwapExecutable(exe, download);

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(Updater.OldPath(exe)));
        Assert.False(File.Exists(download));
    }

    [Fact]
    public void Failed_swap_restores_the_original_executable()
    {
        string exe = Write("SnowWindow.exe", "old");
        string missing = Path.Combine(_dir, "SnowWindow.exe.download");

        Assert.Throws<FileNotFoundException>(() => Updater.SwapExecutable(exe, missing));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(Updater.OldPath(exe)));
    }

    [Fact]
    public void Cleanup_removes_leftovers_from_a_previous_update()
    {
        string exe = Write("SnowWindow.exe", "current");
        Write("SnowWindow.exe.old", "previous");
        Write("SnowWindow.exe.download", "partial");

        Assert.Equal(2, Updater.CleanupLeftovers(exe));
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(Updater.OldPath(exe)));
        Assert.False(File.Exists(Updater.DownloadPath(exe)));
        Assert.Equal(0, Updater.CleanupLeftovers(exe));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
