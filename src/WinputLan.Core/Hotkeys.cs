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
}
