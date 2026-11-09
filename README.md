# Snow Window ❄️

A Windows tray app that makes snow fall across the desktop and every window, and pile up on window top edges.

**[Download (Windows 10/11, 64-bit)](https://github.com/jeiel85/snow-window/releases/latest/download/SnowWindow-win-x64.exe)** · [Landing page](https://jeiel85.github.io/snow-window/) · [All releases](https://github.com/jeiel85/snow-window/releases)

Just run the downloaded exe — no installer and no .NET runtime needed. The exe is not code-signed, so on first launch Windows may show "Windows protected your PC"; click **More info → Run anyway**.

- Snow falls across every monitor (over the top of windows)
- **It piles up on window top edges** and at the bottom of the screen (just above the taskbar). Parts hidden behind another window don't collect snow.
- Shrinking a window spills the overflowing snow off the edge, and shaking a window fast knocks the snow off. When a window is closed or minimized, its snow falls down.
- Moving the mouse over piled snow scatters it, and falling flakes get swept aside by the cursor.
- Mouse and keyboard input passes straight through, so it never gets in the way of your work.
- Snow pauses automatically on a monitor where a full-screen video or game is running.

## Usage

Right-click the snowflake icon in the notification area to open the menu. Left-click toggles the snow on and off.

| Menu item | Description |
|---|---|
| Snow (눈 내리기) | Turn snow on/off |
| Snow amount (눈의 양) | Light (조금) / Normal (보통) / Heavy (펑펑) |
| Scatter snow with mouse (마우스로 눈 흩날리기) | Turn mouse interaction on/off |
| Smooth motion (부드러운 움직임, 60fps) | Default is 30fps. 60fps is smoother but roughly doubles the CPU and GPU load |
| Clear piled snow (쌓인 눈 치우기) | Removes all piled snow |
| Run at Windows startup (Windows 시작 시 실행) | Registers/unregisters `HKCU\...\Run` |
| Open log folder (로그 폴더 열기) | `%LOCALAPPDATA%\SnowWindow` |

Settings are saved in `%APPDATA%\SnowWindow\settings.json`.

## Build and run

Requirements: Windows 10 1607 or later, .NET 10 SDK

```bash
dotnet build SnowWindow.sln -c Release
dotnet test SnowWindow.sln
```

Executable: `src/SnowWindow/bin/Release/net10.0-windows/win-x64/SnowWindow.exe`

Release build (single self-contained exe, about 50 MB):

```bash
dotnet publish src/SnowWindow -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Upload `publish/SnowWindow.exe` to the release as `SnowWindow-win-x64.exe` — the download links on the landing page and in this README point to that asset name.

The landing page is the single file `docs/index.html`, served by GitHub Pages from the `/docs` folder on `main`.

## Diagnostics

- `SnowWindow.exe --dump-windows > windows.txt`: prints which windows are recognized as snow-holding (`[snow]`) and which only occlude (`[    ]`)
- Environment variable `SNOWWINDOW_PROFILE=1`: writes per-stage frame timings to the log every 10 seconds

## How it works

```
SnowEngine (dedicated thread, 30/60fps)
 ├─ WindowChangeWatcher  Detects window moves/show/hide/z-order changes via WinEvent → rescans only when something changed (and every second as a safety net)
 ├─ WindowTracker        Collects the window list (z-order, actual DWM borders) with EnumWindows
 ├─ SnowSimulation       Flakes, landing, piles (per-column heightmaps), avalanche smoothing, spill-off, mouse scattering. No Win32 dependency → covered by unit tests
 └─ SnowRenderer         One transparent click-through topmost window per monitor + DirectComposition swap chain + Direct2D (sprite batches for flakes)
TrayApp (UI thread)      Tray menu, settings save, startup registration
```

- The overlay window uses `WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST`, so it has no GDI buffer and lets mouse input through.
- All coordinates are physical pixels on a Per-Monitor V2 basis.
- `tools/make_icon.py` generates the app icon (`src/SnowWindow/Assets/snow.ico`).
