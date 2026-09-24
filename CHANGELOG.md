# Changelog

All notable changes to Winput LAN are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

## [0.3.11] - 2026-09-24

### Changed

- While input goes to the other PC, the controller's mouse cursor stays where it was instead of jumping to the centre of the main screen. Only a cursor within 50 px of a monitor edge is nudged inward, so motion towards that edge still reaches the other PC.

## [0.3.10] - 2026-09-24

### Fixed

- While controlling another PC, the mouse wheel reaches it even when another wheel tool (e.g. SmoothMice) installed its hook after Winput LAN. The mouse hook is moved back to the front of the chain on every switch to the remote PC.

## [0.3.9] - 2026-09-23

### Changed

- The notification-area icon stays visible for as long as the app runs, including while the window is open or maximized.
- Minimize only minimizes the window to the taskbar; hiding to the notification area now happens only on close ("Continuar em segundo plano ao fechar").

## [0.3.8] - 2026-09-23

### Fixed

- Connecting through the switch shortcut now also moves input to the other PC as soon as the session is ready, instead of needing a second press.
- After a disconnect, the controlled PC keeps showing the known controller by name, as "Aguardando conexão · Máquina já reconhecida", instead of "Máquina vinculada · Código novo necessário".

## [0.3.7] - 2026-09-23

### Added

- Red "Desconectar" button (Lucide unlink icon) to the left of "Conectar outra máquina", shown only while a session or request is active. It closes the connection on either side and returns input to this PC. Trust is kept: reconnecting is still recognized and honours automatic acceptance.

## [0.3.6] - 2026-09-23

### Added

- Preference "Permitir automaticamente conexões conhecidas" (off by default): a recognized controller, with a valid trust key and unchanged certificate, starts its session without the approval prompt. Unknown machines still need the code and approval.

### Changed

- Default window height is 862 px so the dashboard fits without a scrollbar; on first display the window grows further if the content still overflows, up to the work area.

## [0.3.5] - 2026-09-23

### Changed

- Default window size reduced by 20% (1251 × 805).

### Fixed

- Hovering some options no longer shows an empty white tooltip; tooltips are removed.

## [0.3.4] - 2026-09-23

### Changed

- Icons switched to a single library (Lucide, ISC licence) drawn by a new `Icon` control.
- Tighter type scale and font weights; buttons are more compact (40 px primary/secondary).
- "Controlar outra máquina" renamed to "Conectar outra máquina", now with a link icon.
- Machine rows drop the mouse/keyboard icons and their divider; the shortcuts list drops its status badges.
- README trimmed to end-user guidance.

### Fixed

- Declining or missing the UAC prompt for "Permitir controlar apps de administrador" now explains why the option stayed off, and the title bar shows "Admin" when the app runs elevated.

## [0.3.3] - 2026-09-23

### Fixed

- Shift combined with navigation keys (Home, End, arrows, Page Up/Down, Insert, Delete) now selects text on the controlled PC. Extended keys were injected as their numeric-keypad twins, and with Num Lock on Windows released Shift around them.

### Changed

- Heartbeat every 3 seconds (was 5); a lost link is detected after 9 seconds without reply (was 15).

## [0.3.2] - 2026-09-23

### Changed

- The switch-shortcut hint appears only on the machine opposite to the one receiving input.

## [0.3.1] - 2026-09-23

### Security

- Third input safeguard: the controlled PC injects input only while the controller has announced control focus on it. The controller announces focus before the first input of every activation.

### Fixed

- Intermittent disconnects: frames sent concurrently (heartbeat, input, ACK) could reach the wire out of sequence order and be rejected by the receiver.
- Label colour of primary buttons (e.g. "Controlar outra máquina") now matches their icon.
- The dialog close (×) button has a full 44 px click target instead of a sliver next to the title.

### Changed

- "Esta máquina" card: "Renovar código" sits next to the code with a rotate icon; preferences are one aligned column with app-styled checkboxes and an update-frequency chip.
- No chevron on this PC's own row.

## [0.3.0] - 2026-09-23

### Added

- Recognized machines: after one code pairing, the controller reconnects with only an approval click on the other PC. The code is required again when the controlled PC renews its code, or when either machine's identity or certificate changes.
- "Iniciar com o Windows": starts minimized in the notification area (elevated logon task when admin mode is on, so no UAC prompt at sign-in).
- Machine list shows real machine names, a "Controladora" badge on the controlling PC, and moves the green "Ativa" state to whichever machine is receiving input, on both PCs.

### Changed

- Updates button uses the Windows "Sync" system icon.
- The approval prompt brings the window forward even when the app is in the notification area.

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
