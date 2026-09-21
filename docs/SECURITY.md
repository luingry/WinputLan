# Security model

## Pairing

Each instance has a self-signed RSA identity certificate. The private key is
stored only as a DPAPI CurrentUser-protected PFX. During first connection the
TLS peer certificates and fresh nonces form a canonical transcript. SHA-256
derives the six-digit SAS; both users must verify the same code and confirm.
The SAS is deliberately absent from every frame. The bilateral transcript
digest and peer certificate fingerprint are stored as a DPAPI-protected pin.

## Transport

TLS 1.2 is mutual: each side presents its certificate. Pairing mode accepts a
new certificate only for the explicit first pairing flow. Normal reconnects
require the pinned SHA-256 certificate fingerprint. Frames have a magic,
version, bounded length, type, and sequence number; malformed input is rejected
before allocation beyond the 64 KiB cap.

## Input and privacy

Injected events carry a private `dwExtraInfo` marker and are ignored by the
low-level hooks. The in-memory log stores UTC time, origin, destination, event
kind, and status only. It has no payload field and is bounded to 500 entries.
There is no clipboard, text extraction, screen capture, or telemetry.

## Firewall and updater

The installer adds one visible inbound TCP rule: `Winput LAN (Private TCP)`,
Private profile only, the installed program path, and the configured port. The
updater accepts only HTTPS GitHub manifest/asset URLs, a newer SemVer, a valid
SHA-256, and an Authenticode-trusted asset. A missing release certificate keeps
the update path closed rather than silently installing an unsigned binary.

## Known boundaries

UIPI/UAC and the secure desktop can prevent low-level hooks or `SendInput` from
crossing integrity levels. `Ctrl+Alt+Del` and password prompts are not
automated. Users should stop the app if an unexpected input route appears;
disconnect cleanup releases tracked keys/buttons and defaults to local input.
