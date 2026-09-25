# Testing and evidence

## Automated

`tests/WinputLan.Tests` is a dependency-light net8 console suite. It covers:

- frame and input binary round trips plus malformed bounds;
- transcript, pin code and HKDF compatibility, plus access-proof certificate-substitution and purpose/replay rejection;
- DPAPI abstraction round trip and corrupt pin fail-safe;
- queue capacity, mouse-only coalescing, and key/button order;
- queue signal integrity after coalesce/clear, LAN IP selection, latency-window throttling, and audit-log rate limiting;
- global hotkey parsing and reserved combinations;
- bounded metadata-only logging;
- corrupt/invalid config rejection;
- newer SemVer, pinned GitHub HTTPS, SHA-256 and RSA manifest invariants,
  including tampering of every signed field, signature, algorithm and key id;
- capped reconnect backoff.

The net48 loopback harness starts two independent transports and certificates.
It proves that a wrong high-entropy code/proof produces no target prompt, a
correct code can be denied, target cancellation clears its passive prompt and
prevents late acceptance, target approval creates a controller-send/target-receive
session, and a new session works after closure. It rejects forbidden target input and measures
the same TLS session, 30 serial timestamped input events, and target ACKs with a
test-only legacy 2ms polling drain versus the production signal-driven drain;
the local loopback p95 assertion is <=50ms. It does not call a
physical keyboard or mouse and is not evidence for two-PC Wi-Fi latency.
It also round-trips the persisted background preference and asserts the
close/explicit-Exit lifecycle decisions, including exactly-once cleanup.

## Commands

```powershell
dotnet build WinputLan.sln -c Release
dotnet run --project tests/WinputLan.Tests/WinputLan.Tests.csproj -c Release --no-build
tests/WinputLan.Loopback/bin/Release/net48/WinputLan.Loopback.exe
```

The WPF process was launched for a startup smoke in the development
environment. Full screenshot capture is environment-dependent; the source
UI, accessibility names, keyboard focus triggers, and generated direction
assets are checked into the repo. Physical two-PC input acceptance, UIPI/UAC,
and signed-update acceptance remain release-gate work.

## Stability regressions (0.3.17)

`StabilityTests` exercises inbound/outbound TLS cancellation, explicit disconnect,
shutdown and handshake deadlines against silent TCP peers, then reuses the same
listener port. A TLS peer that stops reading proves that the heartbeat watchdog
closes a congested connection independently of the blocked writer.

Controlled gates exercise an input already removed from the queue during a rapid
remote/local/remote switch, then require fresh input to arrive without the stale
press. Queue saturation while a key is held must disconnect and release the key
without passing its dropped release through the local hook. These tests use a
recording sink and never inject physical input. Core tests also assert held-key
repeat ownership and four-Hz auditing of relative movement.

A receiver whose injection is held mid-drag must merge the 200 relative moves
queued behind it into one move with the exact total distance, and inject the
button release only after that move. Core tests also check that this merging
never crosses a click, a control frame, or a transport state change.

Motion pacing sends 100 relative samples at 1000 Hz through a real router and
TLS session and requires the full distance with far fewer frames (about 25 at
the 4 ms interval). With a 2 s interval, a click queued behind paced motion must
still arrive at once, right after that motion. Core tests check that the pacing
merge never crosses a click or an epoch, and that the tracked cursor on the
target keeps to the ClipCursor rectangle and never enters gaps between monitors.
