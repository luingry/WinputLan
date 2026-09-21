# Winput LAN protocol v1

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
`HeartbeatAck`, `Goodbye`, and `Error`.

The fixed input record is 32 bytes: kind, reserved byte, flags, X, Y,
mouse-data, virtual key, scan code, UTC ticks, and a reserved uint. Keyboard
down/up and mouse button down/up are never coalesced. Only a tail mouse move
may be replaced by a newer move.

Hello is a line-safe `deviceId|certificateFingerprint|nonce-base64` field. The
pairing digest is sent only as a bilateral confirmation token; the six-digit
SAS is local display state. A peer that fails version, length, sequence, TLS,
or pin checks is disconnected and all remote input state is released.
