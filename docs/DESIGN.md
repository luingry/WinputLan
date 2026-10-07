# Design direction

`assets/brand/prototype-master.png` is the literal desktop composition baseline,
not loose inspiration. At the 1564×1006 primary viewport the application uses a
54px product chrome and a full-width dashboard ordered as
Máquinas → Atalhos → Registro. Rows, keycaps and the table are grouped with backgrounds and spacing,
not wrapper borders. Pairing is a modal overlay so it never displaces those
three operational sections. The chrome contains icon-only Verificar atualizações (refresh)
and Reportar problema (bug) actions to the left and right of the version.
Admin and local network status labels are hidden.
Máquinas, Atalhos and Registro are continuous dashboard sections, not
navigation destinations. A secondary "Reportar problema" action opens a separate
native report window without changing dashboard geometry. The shortcut editor uses the same in-window overlay,
including focusable text fields, error copy, cancel and save actions.

The palette is `#101519` / `#171C20` / `#20262B`, with `#79D88B` reserved for
connection, selection and primary action. Icons come only from Lucide (ISC),
stored as 24×24 stroke geometries in `App.xaml` and drawn by `Controls/Icon`.
Type scale: page 26 / dialog 22 / section 18 / card 17 / body 14 / secondary 13 /
overline 11, SemiBold for titles and values only. The
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
labels or icon tooltips and automation names, and the peer address input has an automation name. No content typed
into the shared keyboard is ever surfaced in the log.

Report emails inherit the graphite/green palette, Segoe UI with Arial/Helvetica
fallbacks, and the executable's logo as an inline PNG attachment. Their centered
container is fluid up to 600px, using tables and inline styles with an Outlook
fallback. The reading order is brand header, report title/protocol/capture time,
description, reproduction steps, diagnostic summary/JSON attachment, and footer.
The plain-text alternative carries the same information; empty steps and absent
metadata have explicit labels. Browser previews use synthetic reports only.
