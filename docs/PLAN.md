# MVP plan

## Delivered in 0.1.0

- Symmetric WPF .NET Framework 4.8 process with a single active target.
- Low-level keyboard/mouse capture, injected-event filtering, bounded queue,
  mouse-move coalescing only, ordered keys/buttons, and release-on-failure.
- TLS 1.2 mutual certificates, transcript-derived six-digit SAS, bilateral
  confirmation, certificate pinning, and DPAPI-protected pin material.
- Minimal dark neutral UI with muted green functional accent, editable global
  hotkey defaults, in-memory privacy-safe transaction log, status/latency
  placeholders, and explicit update state.
- Explicit Private-profile firewall rule, portable installer notes, SemVer,
  changelog, manifest validation, Inno Setup, and CI/release workflows.

## Next bounded increments

1. Persist a small peer list while retaining one active target.
2. Add an in-app endpoint editor and reconnect status/latency measurement.
3. Exercise a signed production update manifest from a public release.
4. Exercise two physical Windows machines, including UAC/UIPI and display
   scaling, before calling the release physically accepted.

## Out of scope

Discovery, subnet scans, broadcast, cloud control, telemetry, clipboard or
typed-content capture, secure-desktop automation, and privileged-by-default
execution.
