# Design direction

`assets/brand/prototype-master.png` is the literal desktop composition baseline,
not loose inspiration. At the 1564×1006 primary viewport the application uses a
54px product chrome, a 286px left rail, and a dense dashboard ordered as
Máquinas → Atalhos → Registro. The rail's active state has a green leading
stripe; rows, keycaps and the table are grouped with backgrounds and spacing,
not wrapper borders. Pairing is a modal overlay so it never displaces those
three operational sections. The rail deliberately contains only Atualizações:
Máquinas, Atalhos and Registro are continuous dashboard sections, not
navigation destinations. The shortcut editor uses the same in-window overlay,
including focusable text fields, error copy, cancel and save actions.

The palette is `#101519` / `#171C20` / `#20262B`, with `#79D88B` reserved for
connection, selection and primary action. Icons are local outline paths and the
only persisted peer shown is the actual pinned peer; otherwise the machines
component renders its empty state. The dashboard remains scrollable below the
primary viewport instead of clipping actions or the input log.

The generated prototype and icon masters live in `assets/brand`. The normalized
two-tone icon is `icon-normalized.png`, and `winput-lan.ico` is the real icon
used by the executable/project. The master images are retained for visual
traceability and are not used as an unbounded runtime dependency.

Interactive states include hover, pressed, keyboard focus, disabled, pairing,
offline, connected, and failed. The app does not animate state transitions;
this gives reduced-motion-safe behavior by default. All actions have readable
button text and the peer address input has an automation name. No content typed
into the shared keyboard is ever surfaced in the log.
