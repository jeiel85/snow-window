# Backlog

Improvements that are open but not urgent. Each item: what, why, impact.

- [ ] **Choose which monitors get snow** — GPU cost is mostly a fixed per-monitor cost (measured: 3 monitors, AMD 780M, 30fps, about 7% of the GPU 3D engine whether Light or Heavy). An option like "main monitor only" cuts it to about 1/3. Impact: settings, tray menu, `SnowEngine` monitor list
- [ ] **Update only the regions that changed (dirty rect)** — currently each frame clears and presents the whole screen. Updating only the areas with flakes and piles could lower the fixed GPU cost. Impact: `SnowRenderer` (needs Present1 and buffer-content preservation handling)
- [ ] **Fine clipping when a higher window's bottom edge covers only part of a pile** — right now, any overlap with the pile's height range hides that whole column. Impact: `SnowSimulation.ComputeExposure`, the renderer
- [ ] **Check on screen with mixed DPI (e.g. 100% + 150%)** — only verified in a 3-monitor setup that's all 100%. Window piles change column width and get cleared when a window moves to a monitor with a different scale. Impact: `SnowSimulation.SyncWindows`
- [ ] **Unhook the WinEvent hooks while snow is off** — when off, messages are processed only every 0.5s, so events pile up briefly in the queue (harmless but wasteful). Impact: `SnowEngine`, `WindowChangeWatcher`
- [ ] **Clickable update notification** — tray balloons that move to the Windows notification center don't call back into the app when clicked, so the notification only points to the tray menu. A real Windows toast with activation (AUMID + COM activator) would let one click install. Impact: `TrayApp`, packaging
- [ ] **Type a city for weather mode** — weather mode finds the location from the IP address only; a VPN or a wrong lookup gives the wrong city. Letting the user type a city (Open-Meteo geocoding) would fix both and avoid sending the IP. Impact: settings UI, `Weather`
- [ ] **Snowballs in the landing-page demo** — the JS demo on `docs/index.html` imitates scattering and shaking but not rubbing snow into snowballs or stacking a snowman, so the page describes a feature its demo can't show. Impact: `docs/index.html` script
- [ ] **Snowballs across mixed DPI** — a snowball keeps the scale of the monitor it was made on, so carrying it to a monitor with a different scale keeps its old size and physics constants. Impact: `SnowSimulation.Snowballs`
