using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinputLan.Core
{
    public struct SemVer : IComparable<SemVer>
    {
        public SemVer(int major, int minor, int patch) { Major = major; Minor = minor; Patch = patch; }
        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }
        public static SemVer Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Version is empty.");
            var match = Regex.Match(value.Trim(), "^(?:v)?(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$");
            if (!match.Success) throw new FormatException("Version must be SemVer major.minor.patch.");
            return new SemVer(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        public int CompareTo(SemVer other) { var major = Major.CompareTo(other.Major); if (major != 0) return major; var minor = Minor.CompareTo(other.Minor); return minor != 0 ? minor : Patch.CompareTo(other.Patch); }
        public override string ToString() { return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch); }
        public override bool Equals(object obj) { return obj is SemVer && CompareTo((SemVer)obj) == 0; }
        public override int GetHashCode() { return Major * 1000000 + Minor * 1000 + Patch; }
        public static bool operator >(SemVer left, SemVer right) { return left.CompareTo(right) > 0; }
        public static bool operator <(SemVer left, SemVer right) { return left.CompareTo(right) < 0; }
        public static bool operator >=(SemVer left, SemVer right) { return left.CompareTo(right) >= 0; }
        public static bool operator <=(SemVer left, SemVer right) { return left.CompareTo(right) <= 0; }
    }

    public sealed class ReleaseManifest
    {
        public string Version { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public string Sha256 { get; set; }
        public string NotesUrl { get; set; }
        public string Algorithm { get; set; }
        public string KeyId { get; set; }
        public string Signature { get; set; }
    }

    public static class ReleaseManifestSignature
    {
        public const string AlgorithmName = "RSA-PKCS1-SHA256";
        public const string KeyIdentifier = "winputlan-ota-rsa-2026-09b";
        // Public half of the release key. The matching private PEM is never stored in this repository.
        public const string PinnedPublicKeyXmlBase64 = "PFJTQUtleVZhbHVlPjxNb2R1bHVzPmxIa3hLZGFQZ0hwTlA3bTVaY0dONys0TUFpNWVqYm9tRmgxRWJENGIvU2dsRVQ2NWxyL2ZHeDdiQUpZQUdWQnZDTmM0VVJHbUtFbk5SRUg1OHpvVUNadEJPVnM3VThqcGROQVFkUzJQalNCemFyYS9KUFh0ZXlGd3c0eUhWRnl5TW9oSGdqWmhmbVllbzRJZVRuVXRFckQrSGh5VGxYekk3QURvdmpXN1FsUFpxV1hZMHZpVmswLzlmcVBJK1BVYVhWRW5rZTdGckZtSnYvQ2xpem1MR3R3cER1UTNiUUp4YWFZaWlvZDFxUjNzTG5yaEMxdzk4cTNpS0lmUndFZGdNa0VuR08wajlxNDU5VW1wbjNnUmhTcDdZNWJPalh0M0xTMDVGd1RGSEZPY1pmQ25tREZwNFpFTTBsdzRpaXhBT1ROa3hiZmZPL2NSNjhMbE5jQnhmYWwrT2lKVERqbm1Fa3Z3Tk5NcktqUUJ3RDlGNUVwOENYZ3NjYTZVOU04djA5cFdQVngyK25wR0hnMWJTQWc4MnlGcnpHNkxXU3owYmZWM0tQdXZLRXRLWXRCU3hvSEdQS3BTMzdxNU5FOUpqY1NTS0crNVdmNWlJZHhWc0MzVjhsWHBtNVMvVlFTL0V0WGxDbCtRcEZYS0VkS1dxZDV6cEwraldVSUxXZEM3PC9Nb2R1bHVzPjxFeHBvbmVudD5BUUFCPC9FeHBvbmVudD48L1JTQUtleVZhbHVlPg==";
        public static string CanonicalPayload(ReleaseManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            return string.Join("\n", new[] { manifest.Version, manifest.AssetName, manifest.AssetUrl, manifest.Sha256, manifest.NotesUrl });
        }
        public static bool Verify(ReleaseManifest manifest) { return Verify(manifest, Encoding.UTF8.GetString(Convert.FromBase64String(PinnedPublicKeyXmlBase64))); }
        public static bool Verify(ReleaseManifest manifest, string publicKeyXml)
        {
            try
            {
                if (manifest == null || manifest.Algorithm != AlgorithmName || manifest.KeyId != KeyIdentifier || string.IsNullOrWhiteSpace(manifest.Signature) || manifest.Signature.Length > 8192) return false;
                var signature = Convert.FromBase64String(manifest.Signature);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalPayload(manifest)), CryptoConfig.MapNameToOID("SHA256"), signature);
                }
            }
            catch { return false; }
        }
    }

    public static class ReleaseManifestValidator
    {
        public static bool TryValidate(ReleaseManifest manifest, string currentVersion, out string reason)
        {
            return TryValidate(manifest, currentVersion, Encoding.UTF8.GetString(Convert.FromBase64String(ReleaseManifestSignature.PinnedPublicKeyXmlBase64)), out reason);
        }
        public static bool TryValidate(ReleaseManifest manifest, string currentVersion, string publicKeyXml, out string reason)
        {
            reason = null;
            if (manifest == null) { reason = "Manifest is missing."; return false; }
            SemVer current, available;
            try { current = SemVer.Parse(currentVersion); available = SemVer.Parse(manifest.Version); }
            catch (Exception ex) { reason = ex.Message; return false; }
            if (available <= current) { reason = "Manifest is not newer than the installed version."; return false; }
            if (manifest.Version.Length > 32) { reason = "Version is too long."; return false; }
            if (string.IsNullOrWhiteSpace(manifest.AssetName) || manifest.AssetName.Length > 160 || !Regex.IsMatch(manifest.AssetName, "^WinputLan-[0-9]+\\.[0-9]+\\.[0-9]+-setup\\.exe$")) { reason = "Asset name is invalid."; return false; }
            if (!manifest.AssetName.Contains(manifest.Version)) { reason = "Asset name does not match version."; return false; }
            Uri assetUri, notesUri;
            if (!TryGitHubUrl(manifest.AssetUrl, 2048, out assetUri) || !assetUri.AbsolutePath.EndsWith("/" + Uri.EscapeDataString(manifest.AssetName), StringComparison.Ordinal)) { reason = "Asset URL is invalid."; return false; }
            if (!TryGitHubUrl(manifest.NotesUrl, 2048, out notesUri) || !notesUri.AbsolutePath.EndsWith("/tag/v" + manifest.Version, StringComparison.Ordinal)) { reason = "Notes URL is invalid."; return false; }
            if (!Regex.IsMatch(manifest.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$")) { reason = "SHA-256 is missing or malformed."; return false; }
            if (manifest.Algorithm != ReleaseManifestSignature.AlgorithmName || manifest.KeyId != ReleaseManifestSignature.KeyIdentifier) { reason = "Manifest signing metadata is invalid."; return false; }
            if (!ReleaseManifestSignature.Verify(manifest, publicKeyXml)) { reason = "Manifest signature is invalid."; return false; }
            return true;
        }
        public static bool TryGitHubUrl(string value, int maxLength, out Uri uri)
        {
            uri = null;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && IsGitHubHost(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && uri.Port == 443;
        }
        public static bool IsGitHubHost(string host)
        {
            return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || string.Equals(host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
