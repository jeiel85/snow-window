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
    private readonly SynchronizationContext _ui;

    public TrayApp()
    {
        _settings = AppSettings.Load();

        _enabledItem = new ToolStripMenuItem("눈 내리기", null, (_, _) => SetEnabled(!_settings.Enabled));
        var intensityMenu = new ToolStripMenuItem("눈의 양");
        foreach (var (level, label) in new[]
                 {
                     (SnowIntensity.Light, "조금"),
                     (SnowIntensity.Normal, "보통"),
                     (SnowIntensity.Heavy, "펑펑"),
                 })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetIntensity(level));
            _intensityItems[level] = item;
            intensityMenu.DropDownItems.Add(item);
        }

        _mouseItem = new ToolStripMenuItem("마우스로 눈 흩날리기", null, (_, _) => SetMouseInteraction(!_settings.MouseInteraction));
        _smoothItem = new ToolStripMenuItem("부드러운 움직임 (60fps, 더 무거움)", null, (_, _) => SetSmoothMotion(!_settings.SmoothMotion));
        var clearItem = new ToolStripMenuItem("쌓인 눈 치우기", null, (_, _) => _engine!.ClearSnow());
        _startupItem = new ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) => ToggleStartup());
        var logItem = new ToolStripMenuItem("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        var exitItem = new ToolStripMenuItem("종료", null, (_, _) => ExitThread());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _enabledItem, intensityMenu, _mouseItem, _smoothItem, clearItem,
            new ToolStripSeparator(), _startupItem, logItem,
            new ToolStripSeparator(), exitItem,
        });
        _menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled();

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
            if (e.Button == MouseButtons.Left)
            {
                SetEnabled(!_settings.Enabled);
            }
        };

        _engine = new SnowEngine(_settings.Enabled, _settings.Intensity, _settings.MouseInteraction, _settings.SmoothMotion);
        _engine.Faulted += message => _ui.Post(_ => OnEngineFaulted(message), null);
        _engine.Start();
        RefreshMenu();
    }

    protected override void ExitThreadCore()
    {
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
        _engine.SetEnabled(enabled);
        SaveSettings();
        RefreshMenu();
    }

    private void SetIntensity(SnowIntensity intensity)
    {
        _settings.Intensity = intensity;
        _engine.SetIntensity(intensity);
        SaveSettings();
        RefreshMenu();
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
        _enabledItem.Checked = _settings.Enabled;
        _mouseItem.Checked = _settings.MouseInteraction;
        _smoothItem.Checked = _settings.SmoothMotion;
        foreach (var (level, item) in _intensityItems)
        {
            item.Checked = level == _settings.Intensity;
        }

        _icon.Text = _settings.Enabled ? "Snow Window — 눈 내리는 중" : "Snow Window — 멈춤";
    }

    private void OnEngineFaulted(string message)
    {
        // 저장된 설정은 그대로 두어 다음 실행 때 다시 시도한다.
        _enabledItem.Checked = false;
        _settings.Enabled = false;
        _icon.Text = "Snow Window — 오류로 멈춤";
        _icon.ShowBalloonTip(8000, "Snow Window", $"{message}\n자세한 내용은 로그 폴더를 확인하세요.", ToolTipIcon.Error);
    }

    private void ShowError(string message) =>
        _icon.ShowBalloonTip(8000, "Snow Window", message, ToolTipIcon.Error);

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Directory) { UseShellExecute = true });
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("SnowWindow.snow.ico")
            ?? throw new InvalidOperationException("내장 아이콘 리소스(SnowWindow.snow.ico)를 찾을 수 없습니다.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
