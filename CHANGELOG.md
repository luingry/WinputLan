# Changelog

All notable changes to Winput LAN are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

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
