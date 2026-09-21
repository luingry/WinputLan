using System;
using System.Globalization;
using System.Linq;
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

        public int CompareTo(SemVer other)
        {
            var major = Major.CompareTo(other.Major); if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor); return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }

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
        public bool AuthenticodeRequired { get; set; }
        public string NotesUrl { get; set; }
    }

    public static class ReleaseManifestValidator
    {
        public static bool TryValidate(ReleaseManifest manifest, string currentVersion, out string reason)
        {
            reason = null;
            if (manifest == null) { reason = "Manifest is missing."; return false; }
            SemVer current, available;
            try { current = SemVer.Parse(currentVersion); available = SemVer.Parse(manifest.Version); }
            catch (Exception ex) { reason = ex.Message; return false; }
            if (available <= current) { reason = "Manifest is not newer than the installed version."; return false; }
            if (string.IsNullOrWhiteSpace(manifest.AssetName) || manifest.AssetName.IndexOfAny(new[] { '\\', '/', ':', '\r', '\n' }) >= 0) { reason = "Asset name is not a file name."; return false; }
            if (string.IsNullOrWhiteSpace(manifest.AssetUrl) || !Uri.TryCreate(manifest.AssetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsGitHubHost(uri.Host)) { reason = "Asset URL must be an HTTPS GitHub URL."; return false; }
            if (!Regex.IsMatch(manifest.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$")) { reason = "SHA-256 is missing or malformed."; return false; }
            if (manifest.AuthenticodeRequired == false) { reason = "Manifest must require Authenticode."; return false; }
            return true;
        }

        public static bool IsGitHubHost(string host)
        {
            return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || string.Equals(host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
