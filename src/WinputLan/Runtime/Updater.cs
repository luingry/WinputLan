using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public interface IAuthenticodeVerifier
    {
        bool IsSignedAndTrusted(string filePath);
    }

    public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
    {
        public bool IsSignedAndTrusted(string filePath)
        {
            try
            {
                var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
                using (var chain = new X509Chain())
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return certificate.NotBefore <= DateTime.UtcNow && certificate.NotAfter >= DateTime.UtcNow && chain.Build(certificate);
                }
            }
            catch { return false; }
        }
    }

    public sealed class GitHubUpdater
    {
        private readonly HttpClient _http;
        private readonly IAuthenticodeVerifier _verifier;

        public GitHubUpdater(HttpClient httpClient, IAuthenticodeVerifier verifier)
        {
            _http = httpClient ?? throw new ArgumentNullException("httpClient");
            _verifier = verifier ?? throw new ArgumentNullException("verifier");
        }

        public async Task<ReleaseManifest> ReadManifestAsync(Uri manifestUri, CancellationToken cancellationToken)
        {
            if (manifestUri == null || manifestUri.Scheme != Uri.UriSchemeHttps || !ReleaseManifestValidator.IsGitHubHost(manifestUri.Host)) throw new InvalidDataException("Manifest URL must be an HTTPS GitHub URL.");
            var bytes = await _http.GetByteArrayAsync(manifestUri).ConfigureAwait(false);
            if (bytes.Length > 64 * 1024) throw new InvalidDataException("Manifest is too large.");
            var serializer = new DataContractJsonSerializer(typeof(ReleaseManifest));
            using (var stream = new MemoryStream(bytes)) return (ReleaseManifest)serializer.ReadObject(stream);
        }

        public async Task<string> DownloadAndValidateAsync(ReleaseManifest manifest, string currentVersion, string destinationDirectory, CancellationToken cancellationToken)
        {
            string reason;
            if (!ReleaseManifestValidator.TryValidate(manifest, currentVersion, out reason)) throw new InvalidDataException(reason);
            Directory.CreateDirectory(destinationDirectory);
            var tempPath = Path.Combine(destinationDirectory, manifest.AssetName + ".download");
            var finalPath = Path.Combine(destinationDirectory, manifest.AssetName);
            var bytes = await _http.GetByteArrayAsync(new Uri(manifest.AssetUrl)).ConfigureAwait(false);
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset SHA-256 does not match the manifest.");
            }
            File.WriteAllBytes(tempPath, bytes);
            if (manifest.AuthenticodeRequired && !_verifier.IsSignedAndTrusted(tempPath)) { File.Delete(tempPath); throw new InvalidDataException("Asset is not Authenticode signed by a trusted certificate."); }
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath);
            return finalPath;
        }
    }
}
