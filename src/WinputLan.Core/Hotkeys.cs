using System;
using System.Collections.Generic;
using System.Linq;

namespace WinputLan.Core
{
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
        Win = 8
    }

    public sealed class HotkeyGesture
    {
        public HotkeyModifiers Modifiers { get; set; }
        public uint VirtualKey { get; set; }

        public static HotkeyGesture Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Hotkey is empty.");
            var parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) throw new FormatException("Hotkey requires at least one modifier and one key.");
            var modifiers = HotkeyModifiers.None;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": modifiers |= HotkeyModifiers.Ctrl; break;
                    case "ALT": modifiers |= HotkeyModifiers.Alt; break;
                    case "SHIFT": modifiers |= HotkeyModifiers.Shift; break;
                    case "WIN": case "META": modifiers |= HotkeyModifiers.Win; break;
                    default: throw new FormatException("Unknown hotkey modifier.");
                }
            }
            uint key;
            var final = parts[parts.Length - 1];
            if (final.Length == 1 && char.IsLetterOrDigit(final[0])) key = (uint)char.ToUpperInvariant(final[0]);
            else if (final.StartsWith("F", StringComparison.OrdinalIgnoreCase) && uint.TryParse(final.Substring(1), out key)) key += 0x6F;
            else if (!uint.TryParse(final, out key)) throw new FormatException("Unsupported virtual key.");
            var gesture = new HotkeyGesture { Modifiers = modifiers, VirtualKey = key };
            HotkeyValidator.Validate(gesture);
            return gesture;
        }

        public override string ToString()
        {
            var values = new List<string>();
            if ((Modifiers & HotkeyModifiers.Ctrl) != 0) values.Add("Ctrl");
            if ((Modifiers & HotkeyModifiers.Alt) != 0) values.Add("Alt");
            if ((Modifiers & HotkeyModifiers.Shift) != 0) values.Add("Shift");
            if ((Modifiers & HotkeyModifiers.Win) != 0) values.Add("Win");
            values.Add(VirtualKey >= 0x70 && VirtualKey <= 0x7B ? "F" + (VirtualKey - 0x6F) : ((char)VirtualKey).ToString());
            return string.Join("+", values);
        }
    }

    public static class HotkeyValidator
    {
        public static void Validate(HotkeyGesture gesture)
        {
            if (gesture == null) throw new ArgumentNullException("gesture");
            if (gesture.Modifiers == HotkeyModifiers.None) throw new ArgumentException("A global hotkey requires a modifier.", "gesture");
            if (gesture.VirtualKey < 1 || gesture.VirtualKey > 0xFE) throw new ArgumentException("Virtual key is outside the supported range.", "gesture");
            if ((gesture.Modifiers & HotkeyModifiers.Win) != 0 && (gesture.Modifiers & HotkeyModifiers.Ctrl) == 0) throw new ArgumentException("Win-only combinations are reserved by Windows.", "gesture");
        }
    }

    public enum HotkeyAction
    {
        SelectLocal,
        SelectRemote
    }

    public sealed class HotkeyBinding
    {
        public HotkeyAction Action { get; set; }
        public HotkeyGesture Gesture { get; set; }
    }

    // WH_KEYBOARD_LL must leave configured chords visible to RegisterHotKey; this class is deterministic and side-effect free.
    public sealed class HotkeyBypassDetector
    {
        private readonly HashSet<uint> _bypassedKeys = new HashSet<uint>();
        private readonly HashSet<uint> _downKeys = new HashSet<uint>();
        private HotkeyGesture _local;
        private HotkeyGesture _remote;

        public HotkeyBypassDetector(HotkeyGesture local, HotkeyGesture remote) { Reconfigure(local, remote); }

        public void Reconfigure(HotkeyGesture local, HotkeyGesture remote)
        {
            HotkeyValidator.Validate(local); HotkeyValidator.Validate(remote);
            _local = local; _remote = remote; _bypassedKeys.Clear(); _downKeys.Clear();
        }

        public bool ShouldBypass(InputEvent value)
        {
            if (value == null || (value.Kind != InputKind.KeyDown && value.Kind != InputKind.KeyUp)) return false;
            var key = (uint)value.VirtualKey;
            if (value.Kind == InputKind.KeyDown) _downKeys.Add(key); else _downKeys.Remove(key);
            if (IsModifierKey(key) && UsesModifier(key)) return Remember(key, value.Kind);
            if (value.Kind == InputKind.KeyDown && (Matches(_local, key) || Matches(_remote, key))) return Remember(key, value.Kind);
            if (value.Kind == InputKind.KeyUp && _bypassedKeys.Remove(key)) return true;
            return false;
        }

        private bool Remember(uint key, InputKind kind) { if (kind == InputKind.KeyDown) _bypassedKeys.Add(key); else _bypassedKeys.Remove(key); return true; }
        private bool Matches(HotkeyGesture gesture, uint key) { return gesture.VirtualKey == key && CurrentModifiers() == gesture.Modifiers; }
        private bool UsesModifier(uint key) { var modifier = ModifierFor(key); return (_local.Modifiers & modifier) != 0 || (_remote.Modifiers & modifier) != 0; }
        private HotkeyModifiers CurrentModifiers()
        {
            var result = HotkeyModifiers.None;
            foreach (var key in _downKeys) result |= ModifierFor(key);
            return result;
        }
        private static bool IsModifierKey(uint key) { return ModifierFor(key) != HotkeyModifiers.None; }
        private static HotkeyModifiers ModifierFor(uint key)
        {
            if (key == 0x10 || key == 0xA0 || key == 0xA1) return HotkeyModifiers.Shift;
            if (key == 0x11 || key == 0xA2 || key == 0xA3) return HotkeyModifiers.Ctrl;
            if (key == 0x12 || key == 0xA4 || key == 0xA5) return HotkeyModifiers.Alt;
            if (key == 0x5B || key == 0x5C) return HotkeyModifiers.Win;
            return HotkeyModifiers.None;
        }
    }
}
