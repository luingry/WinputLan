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

    public interface IHotkeyChordDetector
    {
        bool TryHandle(InputEvent value, out HotkeyAction? action);
    }

    // Only a completed terminal key is a chord. Modifiers continue to the remote stream unless the chord is actively claimed.
    public sealed class HotkeyBypassDetector : IHotkeyChordDetector
    {
        private readonly HashSet<uint> _terminalKeys = new HashSet<uint>();
        private readonly HashSet<uint> _downKeys = new HashSet<uint>();
        private HotkeyGesture _local;
        private HotkeyGesture _remote;

        public HotkeyBypassDetector(HotkeyGesture local, HotkeyGesture remote) { Reconfigure(local, remote); }

        public void Reconfigure(HotkeyGesture local, HotkeyGesture remote)
        {
            HotkeyValidator.Validate(local); HotkeyValidator.Validate(remote);
            _local = local; _remote = remote; _terminalKeys.Clear(); _downKeys.Clear();
        }

        public bool TryHandle(InputEvent value, out HotkeyAction? action)
        {
            action = null;
            if (value == null || (value.Kind != InputKind.KeyDown && value.Kind != InputKind.KeyUp)) return false;
            var key = (uint)value.VirtualKey;
            if (value.Kind == InputKind.KeyDown) _downKeys.Add(key); else _downKeys.Remove(key);
            if (IsModifierKey(key)) return false;
            if (value.Kind == InputKind.KeyDown)
            {
                if (Matches(_local, key)) { _terminalKeys.Add(key); action = HotkeyAction.SelectLocal; return true; }
                if (Matches(_remote, key)) { _terminalKeys.Add(key); action = HotkeyAction.SelectRemote; return true; }
            }
            if (value.Kind == InputKind.KeyUp && _terminalKeys.Remove(key)) return true;
            return false;
        }

        private bool Matches(HotkeyGesture gesture, uint key) { return gesture.VirtualKey == key && CurrentModifiers() == gesture.Modifiers; }
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
