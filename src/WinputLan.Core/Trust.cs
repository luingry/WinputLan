using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace WinputLan.Core
{
    // A machine that completed a code-based pairing. The trust key is derived from the access code and the
    // certificate/nonce-bound transcript of that pairing, so only the two machines that took part can know it.
    [DataContract]
    public sealed class TrustedPeer
    {
        [DataMember(Order = 1)] public string DeviceId { get; set; }
        [DataMember(Order = 2)] public string Fingerprint { get; set; }
        [DataMember(Order = 3)] public string DisplayName { get; set; }
        [DataMember(Order = 4)] public string Address { get; set; }
        // DPAPI-protected 32-byte trust key.
        [DataMember(Order = 5)] public byte[] ProtectedKey { get; set; }
        [DataMember(Order = 6)] public long CreatedUtcTicks { get; set; }
    }

    public static class TrustKey
    {
        public const int Length = 32;

        public static byte[] Derive(string normalizedCode, byte[] transcript)
        {
            if (!AccessCode.IsValid(normalizedCode)) throw new ArgumentException("Access code is invalid.", "normalizedCode");
            if (transcript == null || transcript.Length == 0) throw new ArgumentException("Transcript is required.", "transcript");
            using (var sha = SHA256.Create())
                return HkdfSha256.Derive(Encoding.UTF8.GetBytes(normalizedCode), sha.ComputeHash(transcript), "winput-lan/trust/v1", Length);
        }

        // Proof for a reconnection transcript (fresh nonces and both TLS fingerprints), keyed by the stored trust key.
        public static byte[] Prove(byte[] key, byte[] transcript, string purpose)
        {
            if (key == null || key.Length != Length) throw new ArgumentException("Trust key is invalid.", "key");
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes("winput-lan/resume/v1\n" + (purpose ?? string.Empty) + "\n").Concat(transcript).ToArray());
        }

        public static bool Verify(byte[] key, byte[] transcript, string purpose, byte[] proof)
        {
            if (key == null || key.Length != Length || proof == null || proof.Length != 32) return false;
            var expected = Prove(key, transcript, purpose);
            var diff = 0;
            for (var i = 0; i < expected.Length; i++) diff |= expected[i] ^ proof[i];
            return diff == 0;
        }
    }

    public static class TrustedPeerList
    {
        public const int MaxControllers = 16;

        // Replaces any earlier record for the same device (a re-pairing refreshes key, certificate and name).
        public static List<TrustedPeer> Upsert(IEnumerable<TrustedPeer> peers, TrustedPeer peer)
        {
            if (peer == null || string.IsNullOrWhiteSpace(peer.DeviceId)) throw new ArgumentException("Peer is invalid.", "peer");
            var list = (peers ?? Enumerable.Empty<TrustedPeer>()).Where(p => p != null && !string.Equals(p.DeviceId, peer.DeviceId, StringComparison.Ordinal)).ToList();
            list.Add(peer);
            return list.OrderByDescending(p => p.CreatedUtcTicks).Take(MaxControllers).ToList();
        }

        // Trust holds only while both the device identity and its TLS certificate are unchanged.
        public static TrustedPeer Match(IEnumerable<TrustedPeer> peers, string deviceId, string observedFingerprint)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(observedFingerprint)) return null;
            return (peers ?? Enumerable.Empty<TrustedPeer>()).FirstOrDefault(p => p != null
                && string.Equals(p.DeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(p.Fingerprint, observedFingerprint, StringComparison.OrdinalIgnoreCase)
                && p.ProtectedKey != null);
        }
    }
}
