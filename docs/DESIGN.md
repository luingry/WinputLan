# Design direction

The UI follows the supplied Winput LAN reference: a quiet left rail, one clear
active target, compact metadata cards, and an operational log. The final asset
palette uses graphite `#35393D` and flat green `#72B47A`; app surfaces remain
neutral gray so green has semantic weight rather than becoming decoration.

The generated prototype and icon masters live in `assets/brand`. The normalized
two-tone icon is `icon-normalized.png`, and `winput-lan.ico` is the real icon
used by the executable/project. The master images are retained for visual
traceability and are not used as an unbounded runtime dependency.

Interactive states include hover, pressed, keyboard focus, disabled, pairing,
offline, connected, and failed. The app does not animate state transitions;
this gives reduced-motion-safe behavior by default. All actions have readable
button text and the peer address input has an automation name. No content typed
into the shared keyboard is ever surfaced in the log.
