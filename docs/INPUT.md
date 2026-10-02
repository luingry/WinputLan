# Input handover

## Held modifiers follow a control switch (0.3.18)

When control switches, the Ctrl, Shift and Alt keys that are still physically held move to the machine taking control:

- `InputRoutingState.SetRemoteActive` moves held handover keys (`ModifierHandover.IsHandoverKey`: VK 0x10–0x12, 0xA0–0xA5) between its local and remote sets and returns them.
- `LowLevelInputCapture.HandOverModifiers` replays the original press (scan code and extended flag, kept in `_heldModifiers`):
  - to the remote: KeyDown through the router, then an injected KeyUp on this PC;
  - back to local: an injected KeyDown on this PC. The remote is released by `ReleaseAll`.
- The physical release then goes to the new owner. The chord's terminal key is suppressed by the hotkey detector, and Win and every other key keep their original owner.

## Alt menu guard (removed in 0.3.20; reimplement only if needed)

A moved Alt has no guard against opening an application's menu bar. The user tested releasing the Ctrl+Alt+Shift switch chord and saw no menu activation, so the guard was removed. Reimplement it only if a menu bar opens after a switch. That is most likely with a chord that has Alt but no Ctrl, such as Alt+Shift+N.

The design that was used in 0.3.19:

1. **Replay Alt first.** Pressed first, the keys pressed after it cancel its menu-on-release. Released first while Ctrl is still held, it is not a menu key: Ctrl+Alt produces `WM_KEYUP`, not `WM_SYSKEYUP`.
2. **Tap a mask key only when Alt moves without Ctrl.** Tap it on each machine after the replayed downs, and on the machine giving up control before its injected ups.
3. **Use an unassigned mask key: VK 0xE8**, the AutoHotkey default. Never use VK 0xFF.

**Pitfall (0.3.18):** the mask tap is itself a chord with the held modifiers. In 0.3.18, a VK 0xFF tap injected under Ctrl+Alt+Shift fired a screenshot shortcut on the controlling PC, because some keyboards and hotkey tools use VK 0xFF. Never inject an extra key while modifiers are held unless it is unavoidable. See `ERRORS.md`, 2026-09-28.

## Edge switching (0.3.22)

Optional ("Trocar computador pelas extremidades da tela"). Geometry and payloads live in `WinputLan.Core.EdgePortal`; the wire format is in `docs/PROTOCOL.md`.

- **Controller to target.** `LowLevelInputCapture.TryLeaveThroughEdge` runs in the mouse hook for local motion. When the motion touches the chosen edge of the primary monitor, the move is swallowed (the cursor stays on the primary even with a monitor beyond that edge) and `SetInputTarget(true, fraction)` sends `EdgePortal` with a placement after `ControlFocus(1)`. `_restore` becomes the spot just inside the edge, so a return by shortcut lands there.
- **Target to controller.** `SendInputSink.DetectEdgeTouch` checks the tracked cursor after each injected delta and records the touch; `PairedInputReceiver` sends `EdgeReached` after the injection. The controller then calls `LowLevelInputCapture.ReturnFromEdge`, which places the local cursor at the same fraction of its own edge instead of `_restore`.
- **Shortcuts are unchanged.** They pass no fraction: the controller restores `_restore` and the target cursor stays put. With edge switching on, the shortcut focus still sends `EdgePortal` without a placement, so the target knows its edge.
- **Only the controller decides (0.3.23).** While the PC controlling this one has edge switching on, this PC's option is hidden in the UI (since 0.3.24; disabled in 0.3.23) and treated as off, so two PCs that control each other never both switch through edges. Every edge switch is logged with its fraction (`Target selected-edge 42%`, `restored-edge 42%` on the controller; `Edge placed 42%`, `touched 42%` on the controlled PC).
- **No ping-pong.** Each side disarms its edge on every switch and re-arms it only after the cursor is seen off the edge; the arriving cursor lands `SpawnInset` pixels inside. A 250 ms cooldown follows every switch.
- **Blocked switches.** A held mouse button (drag, selection), a `ClipCursor` smaller than the virtual screen (games, window move loops), or a previous position off the primary monitor.
- **Stale hook positions after a switch (0.3.24).** Windows may have computed the next hook position from where the cursor was before a switch moved it, and delivers it once the hook thread is free. Let through on an edge return, that move put the cursor back next to where control had left (anchor + motion), undoing the placement. For `SwitchSettle.WindowMs` after a switch, `TryHandleStaleMove` treats a position nearer the old origin than the new one as stale: locally it is dropped; towards the target it is sent as motion from the old origin, so entering through an edge no longer adds the 50 px between the edge and the anchor.
