using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WinputLan.Core
{
    public static class AccessProof
    {
        public static byte[] CanonicalTranscript(string controllerId, string targetId, string controllerFingerprint, string targetFingerprint, byte[] controllerNonce, byte[] targetNonce)
        {
            if (controllerNonce == null || targetNonce == null || controllerNonce.Length != 32 || targetNonce.Length != 32) throw new ArgumentException("Access nonces must be 32 bytes.");
            var values = new[] { controllerId, targetId, controllerFingerprint, targetFingerprint };
            if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Access transcript is incomplete.");
            return Encoding.UTF8.GetBytes("winput-lan/access-proof/v2\n" + controllerId + "\n" + targetId + "\n" + controllerFingerprint + "\n" + targetFingerprint + "\n" + Convert.ToBase64String(controllerNonce) + "\n" + Convert.ToBase64String(targetNonce) + "\n");
        }
        public static byte[] Create(string normalizedCode, byte[] transcript, string purpose)
        {
            if (!AccessCode.IsValid(normalizedCode)) throw new ArgumentException("Access code is invalid.", "normalizedCode");
            using (var sha = SHA256.Create())
            using (var hmac = new HMACSHA256(HkdfSha256.Derive(Encoding.UTF8.GetBytes(normalizedCode), sha.ComputeHash(transcript), "winput-lan/access-proof/v2", 32)))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes((purpose ?? string.Empty) + "\n").Concat(transcript).ToArray());
        }
        public static bool Verify(string normalizedCode, byte[] transcript, string purpose, byte[] proof)
        {
            if (proof == null || proof.Length != 32) return false;
            var expected = Create(normalizedCode, transcript, purpose); var diff = 0;
            for (var i = 0; i < expected.Length; i++) diff |= expected[i] ^ proof[i];
            return diff == 0;
        }
    }
}
