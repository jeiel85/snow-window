# Decisions

Records of decisions that are hard to reverse (security, data sent off the PC, distribution).

## 2026-11-10 · Self-update from GitHub releases

**Decision.** The app checks `releases/latest` of `jeiel85/snow-window` about a minute after launch and then daily, and offers an **Install update** menu item. Installing is user-initiated: download next to the exe, verify, rename the running exe to `.old`, move the new one in, restart with `--after-update` (the new process waits up to 20 s for the old one to release the single-instance mutex).

**Verification.** Size, SHA-256 against the `digest` GitHub publishes for the asset, and the downloaded file's product version against the release tag. If the digest is missing the update is refused. This guards against corrupted or mismatched downloads, **not** against a compromised GitHub account or repository: the exe is not code-signed, so trust is "whatever this repository releases".

**Alternatives considered.**
- *Notify only, open the release page* — simplest, but every update would mean a manual download and replacing the file by hand.
- *Installer-based updaters (Velopack/Squirrel, MSIX)* — proper update channels, but they change distribution from a single portable exe to an installer, and MSIX needs signing.
- *Fully automatic install* — rejected by the user: the app could restart unannounced while they work.

**Limits.** Folders the user cannot write to (e.g. Program Files) and development builds (not single-file) fall back to opening the release page. Windows toasts that moved to the notification center do not reach the app when clicked, so the notification text points to the tray menu item.

## 2026-11-10 · Weather mode: location from IP, weather from Open-Meteo

**Decision.** Opt-in "Snow only when it snows outside". Location is looked up from the IP address (`ipapi.co`, fallback `ipwho.is`) when turned on and then daily; weather comes from Open-Meteo every 15 minutes with coordinates rounded to 2 decimals. Snow intensity follows the WMO weather code (71/77/85 light, 73 moderate, 75/86 heavy) and snowfall ≥ 1 cm/h forces heavy.

**Why IP.** Chosen by the user over typing a city (no setup) and Windows location (needs permission and a WinRT target framework). Trade-off: the IP address goes to a third-party lookup service, accuracy is city-level and wrong behind a VPN.

**Alternatives considered.** City name typed once (no IP sent, but needs a settings dialog and a geocoding call); Windows Location API (most accurate, permission prompt, changes the target framework); Korean Meteorological Administration API (needs an API key).

**Data sent.** See "Network and privacy" in README. Nothing is stored except the on/off setting.
