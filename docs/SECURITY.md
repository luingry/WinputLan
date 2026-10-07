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
There is no clipboard, text extraction, screen capture, or background telemetry.
The optional report window prepares a local diagnostic snapshot and sends it,
together with the user's report text, only on an explicit submission. Operational
metadata and log events use an allowlist; machine names, network addresses,
pairing secrets and input payloads are excluded. Opening the verification loads
Cloudflare Turnstile, which receives normal connection and browser information.

Reports use HTTPS and an unpredictable retry secret held only in memory.
The Worker validates Turnstile and binds it to the hostname, action and report ID.
Missing, empty or malformed tokens are rejected before consuming verification
budgets. An atomic 20-verifications/IP/day budget precedes the shared daily
ceiling, including invalid tokens; stored receipt confirmations bypass both.
Atomic D1 triggers enforce acceptance quotas and commit the report/outbox together;
retries compare fixed-size secret hashes in constant time and preserve identity.
Notifications have fixed headers/recipient and a diagnostic JSON attachment.
Distributed attacks or free-tier limits can still interrupt availability; the app
keeps unconfirmed attempts only until the report window closes.
See `BUG_REPORTS_SETUP.md` for limits and retention.

## Firewall and updater

The installer adds one visible inbound TCP rule: `Winput LAN (Private TCP)`,
Private profile only, the installed program path, and the configured port. The
updater accepts only pinned HTTPS GitHub URLs, a newer SemVer, an exact setup
filename, a valid SHA-256, and a PKCS#1 v1.5 SHA-256 RSA signature over the
canonical manifest fields. The matching private PEM exists only in the GitHub
release secret; the public key is pinned in the application. Redirects are
host-pinned and bounded. Authenticode remains optional for SmartScreen/reputation,
but is not an OTA trust dependency.

## Known boundaries

UIPI/UAC and the secure desktop can prevent low-level hooks or `SendInput` from
crossing integrity levels. `Ctrl+Alt+Del` and password prompts are not
automated. Users should stop the app if an unexpected input route appears;
disconnect cleanup releases tracked keys/buttons and defaults to local input.
