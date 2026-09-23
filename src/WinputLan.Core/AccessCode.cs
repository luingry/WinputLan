using System;
using System.Security.Cryptography;
using System.Text;

namespace WinputLan.Core
{
    // Six symbols from a 32-letter alphabet without look-alikes (I, O, 0, 1): ~30 bits. That is enough because the
    // code never travels on the wire, guesses are online-only (3 per 30 s), it rotates every 10 minutes and the
    // controlled PC must still accept the request.
    public static class AccessCode
    {
        public const int Length = 6;
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public static string Generate()
        {
            var bytes = new byte[Length];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var output = new StringBuilder(Length);
            // 256 is a multiple of 32, so masking the low five bits is unbiased.
            foreach (var value in bytes) output.Append(Alphabet[value & 31]);
            return output.ToString();
        }

        public static bool IsAllowed(char character) { return Alphabet.IndexOf(char.ToUpperInvariant(character)) >= 0; }

        public static string Normalize(string value)
        {
            var text = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (character == ' ' || character == '-') continue;
                text.Append(char.ToUpperInvariant(character));
            }
            return text.ToString();
        }

        public static bool IsValid(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != Length) return false;
            foreach (var character in normalized) if (Alphabet.IndexOf(character) < 0) return false;
            return true;
        }

        public static string Format(string normalized)
        {
            normalized = Normalize(normalized);
            if (normalized.Length != Length) return normalized;
            return normalized.Substring(0, 3) + " " + normalized.Substring(3, 3);
        }
    }

    // Per-slot editing model behind the masked code input. Every operation returns the slot that should take focus.
    public sealed class AccessCodeMask
    {
        private readonly char?[] _slots = new char?[AccessCode.Length];

        public int Length { get { return _slots.Length; } }
        public char? this[int index] { get { return _slots[index]; } }
        public bool IsComplete { get { foreach (var slot in _slots) if (!slot.HasValue) return false; return true; } }

        public string Value
        {
            get
            {
                var text = new StringBuilder(_slots.Length);
                foreach (var slot in _slots) if (slot.HasValue) text.Append(slot.Value);
                return text.ToString();
            }
        }

        // Typed or pasted text: invalid symbols are ignored, letters become uppercase, and a full pasted code
        // always fills from the first slot regardless of where the caret was.
        public int Input(int index, string text)
        {
            var accepted = new StringBuilder();
            foreach (var character in text ?? string.Empty) if (AccessCode.IsAllowed(character)) accepted.Append(char.ToUpperInvariant(character));
            if (accepted.Length == 0) return Clamp(index);
            var position = accepted.Length >= _slots.Length ? 0 : Clamp(index);
            foreach (var character in accepted.ToString())
            {
                if (position >= _slots.Length) break;
                _slots[position++] = character;
            }
            return Clamp(position);
        }

        // Clears the current slot, or the previous one when the current is already empty, and moves back with it.
        public int Backspace(int index)
        {
            index = Clamp(index);
            if (_slots[index].HasValue) { _slots[index] = null; return index; }
            if (index == 0) return 0;
            _slots[index - 1] = null;
            return index - 1;
        }

        public int Delete(int index) { index = Clamp(index); _slots[index] = null; return index; }

        public int FirstEmpty() { for (var i = 0; i < _slots.Length; i++) if (!_slots[i].HasValue) return i; return _slots.Length - 1; }

        public void Clear() { for (var i = 0; i < _slots.Length; i++) _slots[i] = null; }

        private int Clamp(int index) { return Math.Max(0, Math.Min(_slots.Length - 1, index)); }
    }
}
