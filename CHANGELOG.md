# Changelog

All notable changes to Winput LAN are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

## [0.2.5] - 2026-09-23

### Fixed

- Missing notification-area (tray) and window icon in installed copies: the icon is now embedded in the executable.

## [0.2.4] - 2026-09-23

### Changed

- Access code shortened to 6 characters (shown as "ABC DEF"). Both PCs must run 0.2.4 or newer to pair.
- Code field is masked: one box per character, automatic uppercase, look-alike symbols ignored, caret follows typing and backspace, paste fills all boxes and Enter sends the request.

## [0.2.3] - 2026-09-23

### Added

- "Permitir controlar apps de administrador": runs Winput LAN elevated so Task Manager and other elevated windows accept remote input.
- Notification on the controlled PC when Windows blocks remote input (elevated window in focus or UAC prompt).

### Fixed

- Window freezing during control: the input log is recorded passively and only rendered when "Ver logs de input" is open.
- Input log list has a maximum height with its own scrolling.

## [0.2.2] - 2026-09-23

### Changed

- Installer wizard uses the Winput LAN icon and brand art, in Brazilian Portuguese.

## [0.2.1] - 2026-09-23

### Added

- Automatic update checks (daily, weekly or off), download progress, silent install and automatic relaunch.
- Mouse side buttons (back/forward) and horizontal scrolling on the controlled machine.

### Fixed

- Controller cursor no longer moves along with the remote one: it stays pinned while control is remote.
- Smooth remote pointer: relative motion, input hooks on a dedicated thread and sampled latency ACKs.
- Clicks land where the remote cursor is, including on scaled and multi-monitor displays.
- Returning control with the shortcut works and no modifier key stays stuck on either machine.

## [0.2.0] - 2026-09-21

### Added

- Start Menu shortcut and RSA-signed OTA manifests for unsigned public setup releases.
- Passive one-way access flow, background tray option, and input latency instrumentation.

### Fixed

- Pairing overlay close/cancel behavior and the Controlar outra máquina icon/text color.
- Remote mouse stuck in place: pointer movement is no longer suppressed on the controller.
- The app exits after launching a verified update so the setup can replace its files.

## [0.1.0] - 2026-09-21

### Added

- Local-first WPF .NET Framework 4.8 application with one active LAN target.
- Mutual TLS pairing with transcript SAS, bilateral confirmation, certificate
  pinning, and DPAPI-protected private identity/pin material.
- Versioned binary frames, bounded input queue, mouse-only coalescing,
  injected-event filtering, global hotkeys, heartbeat, and fail-safe release.
- Neutral graphite UI with functional green accent, metadata-only transaction
  log, explicit Private firewall rule, updater manifest validation, Inno Setup,
  CI/release scripts, and dependency-light tests.

### Limitations

- No discovery, subnet scan, clipboard, telemetry, cloud control, secure desktop
  automation, or physical two-PC release acceptance yet.
- Physical two-PC release acceptance is still pending.
