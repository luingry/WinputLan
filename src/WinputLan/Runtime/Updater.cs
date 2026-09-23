using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class GitHubUpdater
    {
        private const int ManifestMaxBytes = 64 * 1024;
        private const int AssetMaxBytes = 128 * 1024 * 1024;
        private readonly HttpClient _http;
        public GitHubUpdater(HttpClient httpClient) { _http = httpClient ?? throw new ArgumentNullException("httpClient"); }

        public async Task<ReleaseManifest> ReadManifestAsync(Uri manifestUri, CancellationToken cancellationToken)
        {
            Uri ignored;
            if (manifestUri == null || !ReleaseManifestValidator.TryGitHubUrl(manifestUri.AbsoluteUri, 2048, out ignored)) throw new InvalidDataException("Manifest URL must be pinned HTTPS GitHub.");
            var bytes = await ReadPinnedBytesAsync(manifestUri, ManifestMaxBytes, null, cancellationToken).ConfigureAwait(false);
            var serializer = new DataContractJsonSerializer(typeof(ReleaseManifest));
            using (var stream = new MemoryStream(bytes)) return (ReleaseManifest)serializer.ReadObject(stream);
        }

        public Task<string> DownloadAndValidateAsync(ReleaseManifest manifest, string currentVersion, string destinationDirectory, CancellationToken cancellationToken)
        {
            return DownloadAndValidateAsync(manifest, currentVersion, destinationDirectory, null, cancellationToken);
        }

        public async Task<string> DownloadAndValidateAsync(ReleaseManifest manifest, string currentVersion, string destinationDirectory, IProgress<int> percent, CancellationToken cancellationToken)
        {
            string reason;
            if (!ReleaseManifestValidator.TryValidate(manifest, currentVersion, out reason)) throw new InvalidDataException(reason);
            var bytes = await ReadPinnedBytesAsync(new Uri(manifest.AssetUrl), AssetMaxBytes, percent, cancellationToken).ConfigureAwait(false);
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset SHA-256 does not match the manifest.");
            }
            Directory.CreateDirectory(destinationDirectory);
            var tempPath = Path.Combine(destinationDirectory, manifest.AssetName + ".download");
            var finalPath = Path.Combine(destinationDirectory, manifest.AssetName);
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tempPath, finalPath);
                return finalPath;
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }

        private async Task<byte[]> ReadPinnedBytesAsync(Uri initialUri, int maxBytes, IProgress<int> percent, CancellationToken cancellationToken)
        {
            var current = initialUri;
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                Uri ignored;
                if (!ReleaseManifestValidator.TryGitHubUrl(current.AbsoluteUri, 2048, out ignored)) throw new InvalidDataException("Redirect left the pinned GitHub hosts.");
                using (var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        if (response.Headers.Location == null) throw new InvalidDataException("Redirect has no location.");
                        current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("GitHub returned " + (int)response.StatusCode + ".");
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > maxBytes) throw new InvalidDataException("Download is too large.");
                    var total = response.Content.Headers.ContentLength;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream(total.HasValue ? (int)total.Value : 0))
                    {
                        var chunk = new byte[81920];
                        var lastPercent = -1;
                        int read;
                        while ((read = await source.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Download is too large.");
                            buffer.Write(chunk, 0, read);
                            if (percent != null && total.HasValue && total.Value > 0)
                            {
                                var done = (int)(buffer.Length * 100 / total.Value);
                                if (done != lastPercent) { lastPercent = done; percent.Report(done); }
                            }
                        }
                        return buffer.ToArray();
                    }
                }
            }
            throw new InvalidDataException("Too many GitHub redirects.");
        }
    }
}
