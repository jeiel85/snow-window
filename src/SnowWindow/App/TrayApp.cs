using System.Diagnostics;
using System.Text.Json;
using SnowWindow.Simulation;

namespace SnowWindow.App;

/// <summary>알림 영역 아이콘과 메뉴. 설정을 저장하고 눈 엔진을 켜고 끈다.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly SnowEngine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _mouseItem;
    private readonly ToolStripMenuItem _smoothItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Dictionary<SnowIntensity, ToolStripMenuItem> _intensityItems = new();
    private readonly ToolStripMenuItem _intensityMenu;
    private readonly SynchronizationContext _ui;

    // 업데이트: 시작 1분 뒤 처음 확인하고, 그 뒤로 하루에 한 번 확인한다. 설치는 사용자가 누를 때만.
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private readonly ToolStripMenuItem _installItem;
    private readonly ToolStripMenuItem _autoCheckItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private ReleaseInfo? _pendingRelease;
    private Version? _notifiedVersion;
    private bool _checkingUpdate;
    private bool _installingUpdate;
    private Action? _balloonClick;

    // 날씨 연동: 켜 두면 밖에 눈이 올 때만, 밖의 눈 양만큼 내린다.
    private static readonly Dictionary<SnowIntensity, string> IntensityLabels = new()
    {
        [SnowIntensity.Light] = "조금",
        [SnowIntensity.Normal] = "보통",
        [SnowIntensity.Heavy] = "펑펑",
    };
    private readonly WeatherWatcher _weather = new();
    private readonly ToolStripMenuItem _weatherItem;
    private readonly ToolStripMenuItem _weatherStatusItem;
    private bool _announceWeather;

    public TrayApp(bool afterUpdate)
    {
        _settings = AppSettings.Load();
        SyncStartupPath();
        CleanupUpdateLeftovers();

        _enabledItem = new ToolStripMenuItem("눈 내리기", null, (_, _) => SetEnabled(!_settings.Enabled));
        _intensityMenu = new ToolStripMenuItem("눈의 양");
        foreach (var (level, label) in IntensityLabels)
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetIntensity(level));
            _intensityItems[level] = item;
            _intensityMenu.DropDownItems.Add(item);
        }

        _mouseItem = new ToolStripMenuItem("마우스로 눈 흩날리기·뭉치기", null, (_, _) => SetMouseInteraction(!_settings.MouseInteraction));
        _smoothItem = new ToolStripMenuItem("부드러운 움직임 (60fps, 더 무거움)", null, (_, _) => SetSmoothMotion(!_settings.SmoothMotion));
        var clearItem = new ToolStripMenuItem("쌓인 눈 치우기", null, (_, _) => _engine!.ClearSnow());
        _startupItem = new ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) => ToggleStartup());
        var logItem = new ToolStripMenuItem("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        _installItem = new ToolStripMenuItem("업데이트 설치", null, (_, _) => InstallUpdate()) { Visible = false };
        _autoCheckItem = new ToolStripMenuItem("자동으로 확인 (하루 한 번)", null, (_, _) => SetCheckForUpdates(!_settings.CheckForUpdates));
        var updateMenu = new ToolStripMenuItem("업데이트");
        updateMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("지금 확인", null, (_, _) => CheckForUpdates(manual: true)),
            _autoCheckItem,
        });
        _weatherItem = new ToolStripMenuItem("밖에 눈 올 때만 내리기 (날씨 연동)", null, (_, _) => SetFollowWeather(!_settings.FollowWeather));
        _weatherStatusItem = new ToolStripMenuItem("날씨 확인 중…") { Enabled = false, Visible = false };
        var exitItem = new ToolStripMenuItem("종료", null, (_, _) => ExitThread());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _installItem, _enabledItem, _intensityMenu, _mouseItem, _smoothItem, clearItem,
            new ToolStripSeparator(), _weatherItem, _weatherStatusItem,
            new ToolStripSeparator(), _startupItem, updateMenu, logItem,
            new ToolStripSeparator(), exitItem,
        });
        _menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled();
        _installItem.Font = new Font(_menu.Font, FontStyle.Bold);

        // 메뉴(컨트롤)를 만든 뒤라 WinForms 동기화 컨텍스트가 설치되어 있다.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Snow Window",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_settings.FollowWeather)
            {
                ShowBalloon(WeatherStatusText(), ToolTipIcon.Info); // 날씨 연동 중에는 직접 켜고 끄지 않는다
            }
            else
            {
                SetEnabled(!_settings.Enabled);
            }
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            Log.Info("알림을 눌렀습니다");
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };

        _engine = new SnowEngine(EffectiveEnabled, EffectiveIntensity, _settings.MouseInteraction, _settings.SmoothMotion);
        _engine.Faulted += message => _ui.Post(_ => OnEngineFaulted(message), null);
        _engine.Start();

        _weather.Changed += OnWeatherChanged;
        if (_settings.FollowWeather)
        {
            _weather.Start();
        }

        _updateTimer = new System.Windows.Forms.Timer { Interval = (int)FirstUpdateCheckDelay.TotalMilliseconds };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = (int)UpdateCheckInterval.TotalMilliseconds;
            CheckForUpdates(manual: false);
        };
        _updateTimer.Enabled = _settings.CheckForUpdates;

        RefreshMenu();
        if (afterUpdate)
        {
            ShowBalloon($"v{Updater.CurrentVersion}로 업데이트했어요.", ToolTipIcon.Info);
        }
    }

    protected override void ExitThreadCore()
    {
        _updateTimer.Dispose();
        _weather.Dispose();
        _engine.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        Log.Info("종료");
        base.ExitThreadCore();
    }

    private void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        SaveSettings();
        ApplySnowState();
    }

    private void SetIntensity(SnowIntensity intensity)
    {
        _settings.Intensity = intensity;
        SaveSettings();
        ApplySnowState();
    }

    // ---------- 날씨 연동 ----------

    /// <summary>날씨 연동 중에는 밖에 눈이 올 때만 켠다(아직 날씨를 모르면 끈다).</summary>
    private bool EffectiveEnabled => _settings.FollowWeather ? _weather.Current?.Snow != null : _settings.Enabled;

    private SnowIntensity EffectiveIntensity =>
        _settings.FollowWeather ? _weather.Current?.Snow ?? _settings.Intensity : _settings.Intensity;

    private void ApplySnowState()
    {
        _engine.SetEnabled(EffectiveEnabled);
        _engine.SetIntensity(EffectiveIntensity);
        RefreshMenu();
    }

    private void SetFollowWeather(bool follow)
    {
        _settings.FollowWeather = follow;
        SaveSettings();
        if (follow)
        {
            _announceWeather = true;
            _weather.Start();
        }
        else
        {
            _weather.Stop();
        }

        Log.Info($"날씨 연동 {(follow ? "켬" : "끔")}");
        ApplySnowState();
    }

    private void OnWeatherChanged(WeatherState state)
    {
        ApplySnowState();
        if (_announceWeather)
        {
            _announceWeather = false;
            ShowBalloon(WeatherStatusText(), state.Code < 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
    }

    private string WeatherStatusText()
    {
        var state = _weather.Current;
        if (state is null)
        {
            return "날씨 확인 중…";
        }

        if (state.Code < 0)
        {
            return "날씨를 확인하지 못했어요. 15분 뒤에 다시 확인합니다.";
        }

        string snow = state.Snow is { } level ? $"눈 {IntensityLabels[level]}" : "눈 안 옴";
        string stale = state.Error != null ? " (최근 확인 실패)" : "";
        return $"{state.Place}: {Weather.Describe(state.Code)} — {snow}{stale}";
    }

    private void SetMouseInteraction(bool enabled)
    {
        _settings.MouseInteraction = enabled;
        _engine.SetMouseInteraction(enabled);
        SaveSettings();
        RefreshMenu();
    }

    private void SetSmoothMotion(bool smooth)
    {
        _settings.SmoothMotion = smooth;
        _engine.SetSmoothMotion(smooth);
        SaveSettings();
        RefreshMenu();
    }

    private static void SyncStartupPath()
    {
        try
        {
            string? previous = StartupRegistration.SyncToCurrentPath();
            if (previous != null)
            {
                Log.Info($"시작 시 실행 경로를 지금 실행 파일로 바꿨습니다 (이전: {previous})");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 경로를 못 맞춰도 앱 실행에는 지장이 없다. 메뉴에서 다시 켜면 지금 경로로 등록된다.
            Log.Error("시작 시 실행 경로를 확인하지 못했습니다", ex);
        }
    }

    private void ToggleStartup()
    {
        bool target = !StartupRegistration.IsEnabled();
        try
        {
            StartupRegistration.Set(target);
            Log.Info($"시작 시 실행 {(target ? "등록" : "해제")}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("시작 프로그램 등록을 바꾸지 못했습니다", ex);
            ShowError($"시작 프로그램 설정을 바꾸지 못했습니다.\n{ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"설정을 저장하지 못했습니다. 다음 실행 때는 이전 설정으로 시작합니다.\n{ex.Message}");
        }
    }

    private void RefreshMenu()
    {
        bool manual = !_settings.FollowWeather;
        _enabledItem.Checked = EffectiveEnabled;
        _enabledItem.Enabled = manual;
        _intensityMenu.Enabled = manual;
        _weatherItem.Checked = _settings.FollowWeather;
        _weatherStatusItem.Visible = _settings.FollowWeather;
        _weatherStatusItem.Text = WeatherStatusText();
        _mouseItem.Checked = _settings.MouseInteraction;
        _smoothItem.Checked = _settings.SmoothMotion;
        _autoCheckItem.Checked = _settings.CheckForUpdates;
        foreach (var (level, item) in _intensityItems)
        {
            item.Checked = level == EffectiveIntensity;
        }

        string status = _settings.FollowWeather
            ? $"날씨 연동 — {WeatherStatusText()}"
            : _settings.Enabled ? "눈 내리는 중" : "멈춤";
        string tip = $"Snow Window — {status}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127]; // 알림 영역 툴팁은 127자까지
    }

    private void OnEngineFaulted(string message)
    {
        // 저장된 설정은 그대로 두어 다음 실행 때 다시 시도한다.
        _enabledItem.Checked = false;
        _settings.Enabled = false;
        _icon.Text = "Snow Window — 오류로 멈춤";
        ShowError($"{message}\n자세한 내용은 로그 폴더를 확인하세요.");
    }

    private void ShowError(string message) => ShowBalloon(message, ToolTipIcon.Error);

    /// <summary>알림을 띄운다. <paramref name="onClick"/>은 이 알림을 눌렀을 때만 실행된다.</summary>
    private void ShowBalloon(string message, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(8000, "Snow Window", message, icon);
    }

    // ---------- 업데이트 ----------

    private void SetCheckForUpdates(bool enabled)
    {
        _settings.CheckForUpdates = enabled;
        _updateTimer.Enabled = enabled;
        SaveSettings();
        RefreshMenu();
    }

    private static void CleanupUpdateLeftovers()
    {
        if (Environment.ProcessPath is { } exe && Updater.CleanupLeftovers(exe) > 0)
        {
            Log.Info("지난 업데이트가 남긴 파일을 지웠습니다");
        }
    }

    private async void CheckForUpdates(bool manual)
    {
        if (_checkingUpdate || _installingUpdate)
        {
            return;
        }

        _checkingUpdate = true;
        try
        {
            CleanupUpdateLeftovers();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await Updater.CheckAsync(cts.Token);
            if (release != null && release.Version > Updater.CurrentVersion)
            {
                _pendingRelease = release;
                _installItem.Text = $"업데이트 설치 (v{release.Version})";
                _installItem.Visible = true;
                if (manual || _notifiedVersion != release.Version)
                {
                    _notifiedVersion = release.Version;
                    Log.Info($"새 버전 v{release.Version}이 있습니다 (지금 v{Updater.CurrentVersion})");
                    // 알림 센터로 넘어간 알림은 눌러도 앱에 전달되지 않으므로 메뉴 위치를 안내한다.
                    ShowBalloon($"새 버전 v{release.Version}이 나왔어요.\n눈송이 아이콘을 오른쪽 클릭해 '업데이트 설치'를 누르세요.",
                        ToolTipIcon.Info, InstallUpdate);
                }
            }
            else
            {
                _pendingRelease = null;
                _installItem.Visible = false;
                if (manual)
                {
                    ShowBalloon($"최신 버전이에요 (v{Updater.CurrentVersion}).", ToolTipIcon.Info);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            Log.Warn("업데이트를 확인하지 못했습니다", ex);
            if (manual)
            {
                ShowError($"업데이트를 확인하지 못했습니다.\n{ex.Message}");
            }
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private async void InstallUpdate()
    {
        var release = _pendingRelease;
        if (release is null || _installingUpdate)
        {
            Log.Info(release is null ? "설치할 업데이트가 없습니다" : "업데이트를 이미 설치하는 중입니다");
            return;
        }

        Log.Info($"업데이트 설치 시작: v{Updater.CurrentVersion} → v{release.Version}");
        if (!Updater.CanSelfUpdate(out string reason))
        {
            Log.Info($"자동 설치를 하지 않습니다: {reason}");
            ShowBalloon($"{reason}\n릴리스 페이지에서 직접 내려받아 주세요.", ToolTipIcon.Info);
            OpenUrl(release.PageUrl);
            return;
        }

        string exe = Environment.ProcessPath!;
        string download = Updater.DownloadPath(exe);
        _installingUpdate = true;
        _installItem.Enabled = false;
        _installItem.Text = $"v{release.Version} 내려받는 중…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var watch = Stopwatch.StartNew();
            await Updater.DownloadAsync(release, download, cts.Token);
            Log.Info($"내려받기·확인 완료 ({release.AssetSize / 1_048_576}MB, {watch.Elapsed.TotalSeconds:0.0}초)");
            Updater.SwapExecutable(exe, download);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"v{release.Version} 업데이트를 설치하지 못했습니다", ex);
            _installingUpdate = false;
            _installItem.Enabled = true;
            _installItem.Text = $"업데이트 설치 (v{release.Version})";
            ShowError($"업데이트를 설치하지 못했습니다.\n{ex.Message}");
            return;
        }

        Log.Info($"v{release.Version}으로 바꿔 끼웠습니다. 새 버전으로 다시 시작합니다");
        try
        {
            Process.Start(new ProcessStartInfo(exe, Updater.AfterUpdateArg) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // 새 exe는 이미 제자리에 있으므로 다음에 켤 때 새 버전이 실행된다.
            Log.Error("새 버전을 실행하지 못했습니다", ex);
            _installItem.Text = $"v{release.Version} 설치됨 — 다시 켜 주세요";
            ShowError($"v{release.Version}을 설치했지만 다시 켜지 못했습니다.\nSnow Window를 종료한 뒤 다시 실행해 주세요.");
            return;
        }

        ExitThread();
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"브라우저를 열지 못했습니다 ({url})", ex);
            ShowError($"브라우저를 열지 못했습니다.\n{url}");
        }
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Directory);
        Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("SnowWindow.snow.ico")
            ?? throw new InvalidOperationException("내장 아이콘 리소스(SnowWindow.snow.ico)를 찾을 수 없습니다.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
