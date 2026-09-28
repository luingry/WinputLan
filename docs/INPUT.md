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
