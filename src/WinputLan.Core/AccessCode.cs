using System;
using System.Security.Cryptography;
using System.Text;

namespace WinputLan.Core
{
    public static class AccessCode
    {
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        public static string Generate()
        {
            var bytes = new byte[10]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var output = new StringBuilder(16); var buffer = 0; var bits = 0;
            for (var i = 0; i < bytes.Length && output.Length < 16; i++)
            {
                buffer = (buffer << 8) | bytes[i]; bits += 8;
                while (bits >= 5 && output.Length < 16) { bits -= 5; output.Append(Alphabet[(buffer >> bits) & 31]); }
            }
            return output.ToString();
        }
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
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != 16) return false;
            foreach (var character in normalized) if (Alphabet.IndexOf(character) < 0) return false;
            return true;
        }
        public static string Format(string normalized)
        {
            normalized = Normalize(normalized); if (normalized.Length != 16) return normalized;
            return normalized.Substring(0, 4) + " " + normalized.Substring(4, 4) + " " + normalized.Substring(8, 4) + " " + normalized.Substring(12, 4);
        }
    }
}
