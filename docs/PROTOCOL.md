# Winput LAN protocol v2

The transport is a persistent TLS 1.2 stream. Every frame is little-endian:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 2 | magic `WL` (`0x4C57` little-endian) |
| 2 | 1 | protocol version (`1`) |
| 3 | 1 | frame type |
| 4 | 4 | payload length, `0..65536` |
| 8 | 8 | monotonically increasing sequence |
| 16 | n | payload |

Types are `Hello`, `PairingOffer`, `PairingConfirm`, `Input`, `Heartbeat`,
`HeartbeatAck`, `Goodbye`, `Error`, `ReleaseAll`, `InputAck`, `ControlFocus`,
`EdgePortal` and `EdgeReached`.

The fixed input record is 32 bytes: kind, reserved byte, flags, X, Y,
mouse-data, virtual key, scan code, UTC ticks, and a reserved uint. Keyboard
down/up and mouse button down/up are never coalesced. While remote control is
active the controller pins its own cursor and sends `MouseDelta` (kind 7, X/Y are
pixel deltas); consecutive unsent deltas are summed, never dropped. The target
adds them to a tracked cursor and injects an exact absolute position in physical
pixels. Legacy absolute `MouseMove` (kind 1) is still accepted.

The controlled machine generates and displays a 6-character, unambiguous
Base32 CSPRNG access code (30 bits), formatted `XXX XXX`, below its
LAN IPv4 address. The code is never sent on the wire or written to logs/pins.
This is certificate/nonce-bound challenge-response, not PAKE. The short code is
acceptable because the controller must prove knowledge first (the target never
reveals a code-derived value before a person accepts), each guess costs a new
TLS session, three failures block attempts for 30 s, the code rotates every
10 minutes, and a valid code still requires explicit approval on the target.

The controller sends `PairingOffer` `offer|controllerDeviceId|base64(displayName)|controllerNonce`.
The target returns `PairingConfirm` `challenge|targetDeviceId|targetNonce`.
Both sides canonically construct the v2 transcript from the version, controller
and target IDs, the observed controller and target TLS certificate fingerprints,
and both 32-byte CSPRNG nonces. The controller sends `PairingOffer`
`proof|HMAC` where HMAC-SHA256 is made with an HKDF-SHA256 key derived from the
human code and transcript. The target uses a fixed-time comparison; only a
valid proof creates the passive approval prompt. Different certificates under a
TLS first-session MITM produce different transcripts, so the proof is rejected.

The target approval returns a separate `PairingConfirm`
`accept|targetDeviceId|HMAC` proof bound to the `accept` purpose and same
transcript; the controller verifies it before enabling input. `Error` is a
generic denial and closes the request. Codes expire after ten minutes, renew
after accept or denial (preventing proof replay), and can be renewed by the
target. Three bad attempts impose a 30-second target cooldown. Disconnecting a
pending request cancels the target prompt without permitting a late acceptance.

After approval the controller transport is send-only for `Input` and the target
is receive-only. Forbidden input frames are rejected before injection. Every
accepted key, button and wheel input is answered by `InputAck` carrying its
original UTC ticks for lightweight latency telemetry; motion is sampled at most
once per 100 ms. The benchmark compares its signal-driven drain
against a test-only 2ms polling drain under the same TLS/ACK load. `WriteAsync`
is not followed by a redundant flush, but no latency causality is claimed for
that removal. Failed TLS, code, framing, sequence, or
direction checks close the transport and release remote input state.


## Recognized machines (resume)

A completed code pairing also derives a 32-byte trust key on both sides:
`HKDF-SHA256(code, SHA-256(transcript), "winput-lan/trust/v1")`. The controller stores it
with the target's device id, certificate fingerprint and name; the target stores it per
controller. Both are DPAPI-protected. The `challenge` frame now carries the target's
base64 display name as a fourth field.

To reconnect, the controller sends `PairingOffer` `resume|controllerDeviceId|base64(name)|nonce`.
The target answers only if that device id and the observed TLS fingerprint match a
trusted record; otherwise it sends `Error` `untrusted` and the controller falls back to
the code. The controller also refuses to prove anything to a target whose device id or
fingerprint differs from its record. Proofs are `HMAC-SHA256(trustKey, "winput-lan/resume/v1\n" + purpose + "\n" + transcript)`
over a fresh transcript (new nonces, both fingerprints), with purposes `resume-request`
and `resume-accept`. The target still requires an explicit accept, and a resume does not
consume the visible access code. Renewing the code on the target forgets every trusted
controller.

## Control focus

`ControlFocus` (11) carries one byte: 1 when the controller starts directing mouse and
keyboard to the target, 0 when it takes them back. The target uses it only to show which
machine is receiving input.

## Edge switching (0.3.22)

Only sent while the controller has "Trocar computador pelas extremidades da tela" on. Peers older than 0.3.22 close the session on these frame types, so both PCs must be updated before the option is turned on.

- `EdgePortal` (12), controller to target, right after `ControlFocus(1)`. Four bytes: the target's edge (0 none, 1 left, 2 top, 3 right, 4 bottom), a placement flag (0 or 1), and a little-endian `uint16` fraction along that edge. With the flag set (control arrived through the controller's edge), the target moves its cursor to that fraction of its edge on its primary monitor, `EdgePortal.SpawnInset` pixels inside. Without it (shortcut switch), the cursor stays where it was. Every `ControlFocus` clears the target's edge first.
- Since 0.3.23 the controller also sends `EdgePortal` without a placement outside focus: when the session becomes connected with the option on, and whenever the option or the target edge changes (edge 0 when turned off, only if an edge was announced before). Any `EdgePortal` with an edge locks the option on the target for the session (`PairedInputReceiver.ControllerEdgeSwitchingChanged`); edge 0 or the end of the session unlocks it. Unfocused, it does not arm the target edge.
- `EdgeReached` (13), target to controller. Two bytes: the little-endian `uint16` fraction where the target's tracked cursor touched its edge. The controller ignores it unless it is directing input at the target with edge switching on; otherwise it switches back and puts its own cursor at that fraction of its own edge.

A touch counts only when the cursor was on the primary monitor before the motion, has been off the edge since control arrived, at least `EdgePortal.CooldownMs` have passed, no mouse button is held and the cursor is not confined by `ClipCursor`. The primary monitor is the one at the virtual-screen origin. Positions use physical pixels on both sides.
