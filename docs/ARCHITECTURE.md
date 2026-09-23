# Architecture

```text
controller WH_*_LL hooks -> InputRouter -> signal-driven InputEventQueue -> TLS PeerTransport
                                                        ^                     |
                                                InputAck telemetry     target PairedInputReceiver -> SendInput

WPF UI -> target access code + PairingCoordinator -> TLS request/target approval -> DPAPI PinStore
```

`WinputLan.Core` is netstandard2.0 and contains deterministic protocol,
pairing, config validation, queue, hotkey, privacy log, SemVer, and backoff
logic. The WPF project is net48 and owns Win32 hooks, SendInput, DPAPI,
certificates, sockets, firewall, updater, and the UI.

There is no service process and no elevation requirement. A listener uses the
single configured TCP port. A connection is `TcpClient.NoDelay` plus `SslStream`
with TLS 1.2 and client certificates on both sides. Frame reading runs off the
connect caller; a three-second heartbeat is sent while connected and
the link drops after nine seconds without an acknowledgement. Access sessions
remain explicit: the controlled PC displays its IP above a renewable 80-bit,
16-character Base32 code. PairingCoordinator uses a challenge-response HMAC
over both certificate fingerprints and fresh nonces before exposing a passive
approval popup, then validates a distinct acceptance proof before authorizing
the controller. The displayed IP is selected from an Up,
routed Ethernet/Wi-Fi IPv4 interface; loopback and APIPA are excluded. Each
session has one input direction only:
controller sends and target injects. The background option uses one
notification-area icon with Restore and Exit; Exit alone performs final cleanup.

Input events have fixed-size binary payloads. The queue refuses to reorder
keyboard/button events. Only a consecutive tail `MouseMove` is replaced; a
full queue never evicts a key or button. Any failed remote route clears the
queue, releases tracked state, and returns to local-safe behavior.

The UI is intentionally a compact operating surface: local machine, active
target, pairing, shortcuts, and a metadata-only log. The visual thesis is
neutral graphite grouping with a visible but non-neon green reserved for
active/success/action states. Focus, disabled, hover and pressed states are
defined in `App.xaml`; the app avoids decorative motion and is safe under a
reduced-motion system preference.
