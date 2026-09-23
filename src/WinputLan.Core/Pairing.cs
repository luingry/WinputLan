using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WinputLan.Core
{
    public sealed class PairingTranscript
    {
        public PairingTranscript(string localDeviceId, string remoteDeviceId, string localFingerprint, string remoteFingerprint, byte[] localNonce, byte[] remoteNonce)
        {
            LocalDeviceId = Require(localDeviceId, "localDeviceId");
            RemoteDeviceId = Require(remoteDeviceId, "remoteDeviceId");
            LocalFingerprint = Require(localFingerprint, "localFingerprint");
            RemoteFingerprint = Require(remoteFingerprint, "remoteFingerprint");
            LocalNonce = CopyNonce(localNonce, "localNonce");
            RemoteNonce = CopyNonce(remoteNonce, "remoteNonce");
        }

        public string LocalDeviceId { get; private set; }
        public string RemoteDeviceId { get; private set; }
        public string LocalFingerprint { get; private set; }
        public string RemoteFingerprint { get; private set; }
        public byte[] LocalNonce { get; private set; }
        public byte[] RemoteNonce { get; private set; }

        public byte[] CanonicalBytes()
        {
            var peers = new[]
            {
                new PeerMaterial(LocalDeviceId, LocalFingerprint, LocalNonce),
                new PeerMaterial(RemoteDeviceId, RemoteFingerprint, RemoteNonce)
            }.OrderBy(p => p.DeviceId, StringComparer.Ordinal).ThenBy(p => p.Fingerprint, StringComparer.Ordinal).ToArray();
            var builder = new StringBuilder("winput-lan/pairing/v1\n");
            foreach (var peer in peers)
            {
                builder.Append(peer.DeviceId).Append('\n');
                builder.Append(peer.Fingerprint).Append('\n');
                builder.Append(Convert.ToBase64String(peer.Nonce)).Append('\n');
            }
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        public byte[] Digest()
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(CanonicalBytes());
        }

        public string SasCode()
        {
            var digest = Digest();
            var number = ((uint)digest[0] << 16) | ((uint)digest[1] << 8) | digest[2];
            return (number % 1000000U).ToString("D6");
        }

        public string PinCode()
        {
            var digest = Digest();
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var result = new StringBuilder(12);
            for (var i = 0; i < 12; i++) result.Append(alphabet[digest[i] % alphabet.Length]);
            return result.ToString();
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) throw new ArgumentException("A non-empty single-line value is required.", name);
            return value.Trim();
        }

        private static byte[] CopyNonce(byte[] value, string name)
        {
            if (value == null || value.Length < 16 || value.Length > 64) throw new ArgumentException("Nonce must be 16-64 bytes.", name);
            return value.ToArray();
        }

        private sealed class PeerMaterial
        {
            public PeerMaterial(string deviceId, string fingerprint, byte[] nonce) { DeviceId = deviceId; Fingerprint = fingerprint; Nonce = nonce; }
            public string DeviceId { get; private set; }
            public string Fingerprint { get; private set; }
            public byte[] Nonce { get; private set; }
        }
    }

    public sealed class PairingConfirmation
    {
        public PairingConfirmation(PairingTranscript transcript) { Transcript = transcript ?? throw new ArgumentNullException("transcript"); }
        public PairingTranscript Transcript { get; private set; }
        public bool LocalConfirmed { get; private set; }
        public bool RemoteConfirmed { get; private set; }
        public bool IsComplete { get { return LocalConfirmed && RemoteConfirmed; } }

        public void ConfirmLocal(string displayedCode)
        {
            if (!string.Equals(NormalizeCode(displayedCode), Transcript.SasCode(), StringComparison.Ordinal)) throw new InvalidOperationException("Local SAS confirmation does not match this transcript.");
            LocalConfirmed = true;
        }

        public void ConfirmRemote(string remoteTranscriptDigest)
        {
            var expected = Convert.ToBase64String(Transcript.Digest());
            if (!string.Equals(remoteTranscriptDigest, expected, StringComparison.Ordinal)) throw new InvalidOperationException("Remote pairing confirmation does not match this transcript.");
            RemoteConfirmed = true;
        }

        private static string NormalizeCode(string code)
        {
            return new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        }
    }

    public static class HkdfSha256
    {
        public static byte[] Derive(byte[] secret, byte[] salt, string context, int length)
        {
            if (secret == null || secret.Length == 0) throw new ArgumentException("Secret is required.", "secret");
            if (length < 1 || length > 255 * 32) throw new ArgumentOutOfRangeException("length");
            var info = Encoding.UTF8.GetBytes(context ?? string.Empty);
            byte[] prk;
            using (var extract = new HMACSHA256(salt ?? new byte[32])) prk = extract.ComputeHash(secret);
            var output = new byte[length];
            var previous = new byte[0];
            var offset = 0;
            var counter = (byte)1;
            while (offset < length)
            {
                using (var expand = new HMACSHA256(prk))
                {
                    var blockInput = previous.Concat(info).Concat(new[] { counter }).ToArray();
                    previous = expand.ComputeHash(blockInput);
                }
                var copy = Math.Min(previous.Length, length - offset);
                Buffer.BlockCopy(previous, 0, output, offset, copy);
                offset += copy;
                counter++;
            }
            return output;
        }
    }

    public interface ISecretProtector
    {
        byte[] Protect(byte[] plaintext);
        byte[] Unprotect(byte[] protectedData);
    }

    public sealed class PinRecord
    {
        public string DeviceId { get; set; }
        public string CertificateFingerprint { get; set; }
        public string TranscriptDigest { get; set; }
        public DateTime CreatedUtc { get; set; }
        // Not persisted by PinStore: callers store these in the DPAPI-protected trust records.
        public string DisplayName { get; set; }
        public byte[] TrustKey { get; set; }
        public bool Recognized { get; set; }
    }

    public sealed class PinStore
    {
        private readonly ISecretProtector _protector;
        private byte[] _blob;

        public PinStore(ISecretProtector protector) { _protector = protector ?? throw new ArgumentNullException("protector"); }

        public void Save(PinRecord record)
        {
            Validate(record);
            var text = string.Join("\n", new[]
            {
                "winput-pin-v1",
                record.DeviceId,
                record.CertificateFingerprint,
                record.TranscriptDigest,
                record.CreatedUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            _blob = _protector.Protect(Encoding.UTF8.GetBytes(text));
        }

        public bool TryLoad(out PinRecord record)
        {
            record = null;
            if (_blob == null) return false;
            try
            {
                var parts = Encoding.UTF8.GetString(_protector.Unprotect(_blob)).Split(new[] { '\n' }, StringSplitOptions.None);
                if (parts.Length != 5 || parts[0] != "winput-pin-v1") return false;
                long ticks;
                if (!long.TryParse(parts[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ticks)) return false;
                var candidate = new PinRecord { DeviceId = parts[1], CertificateFingerprint = parts[2], TranscriptDigest = parts[3], CreatedUtc = new DateTime(ticks, DateTimeKind.Utc) };
                Validate(candidate);
                record = candidate;
                return true;
            }
            catch { return false; }
        }

        public void LoadProtectedBlob(byte[] protectedBlob) { _blob = protectedBlob == null ? null : protectedBlob.ToArray(); }
        public byte[] ExportProtectedBlob() { return _blob == null ? null : _blob.ToArray(); }

        private static void Validate(PinRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.DeviceId) || string.IsNullOrWhiteSpace(record.CertificateFingerprint) || string.IsNullOrWhiteSpace(record.TranscriptDigest)) throw new ArgumentException("Pin record is incomplete.", "record");
            if (record.DeviceId.IndexOf('\n') >= 0 || record.CertificateFingerprint.IndexOf('\n') >= 0 || record.TranscriptDigest.IndexOf('\n') >= 0) throw new ArgumentException("Pin record contains an invalid line break.", "record");
        }
    }
}
