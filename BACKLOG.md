# Backlog

Improvements that are open but not urgent. Each item: what, why, impact.

- [ ] **Choose which monitors get snow** — GPU cost is mostly a fixed per-monitor cost (measured: 3 monitors, AMD 780M, 30fps, about 7% of the GPU 3D engine whether Light or Heavy). An option like "main monitor only" cuts it to about 1/3. Impact: settings, tray menu, `SnowEngine` monitor list
- [ ] **Update only the regions that changed (dirty rect)** — currently each frame clears and presents the whole screen. Updating only the areas with flakes and piles could lower the fixed GPU cost. Impact: `SnowRenderer` (needs Present1 and buffer-content preservation handling)
- [ ] **Fine clipping when a higher window's bottom edge covers only part of a pile** — right now, any overlap with the pile's height range hides that whole column. Impact: `SnowSimulation.ComputeExposure`, the renderer
- [ ] **Check on screen with mixed DPI (e.g. 100% + 150%)** — only verified in a 3-monitor setup that's all 100%. Window piles change column width and get cleared when a window moves to a monitor with a different scale. Impact: `SnowSimulation.SyncWindows`
- [ ] **Unhook the WinEvent hooks while snow is off** — when off, messages are processed only every 0.5s, so events pile up briefly in the queue (harmless but wasteful). Impact: `SnowEngine`, `WindowChangeWatcher`
