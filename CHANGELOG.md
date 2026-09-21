# Changelog

All notable changes to Winput LAN are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

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
- Production Authenticode certificate and signed public release are not present.
