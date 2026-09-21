using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class CertificateManager
    {
        private readonly string _path;
        private readonly ISecretProtector _protector;

        public CertificateManager(string directory, ISecretProtector protector)
        {
            _path = Path.Combine(directory, "identity.pfx.dpapi");
            _protector = protector ?? throw new ArgumentNullException("protector");
            Directory.CreateDirectory(directory);
        }

        public X509Certificate2 LoadOrCreate()
        {
            if (File.Exists(_path))
            {
                try
                {
                    var bytes = _protector.Unprotect(File.ReadAllBytes(_path));
                    return new X509Certificate2(bytes, (string)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                }
                catch { /* corrupt identity fails safe by creating a new identity */ }
            }
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=WinputLan local identity", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
                var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
                var pfx = certificate.Export(X509ContentType.Pfx);
                File.WriteAllBytes(_path, _protector.Protect(pfx));
                return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            }
        }

        public static string Fingerprint(X509Certificate2 certificate)
        {
            if (certificate == null) throw new ArgumentNullException("certificate");
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(certificate.RawData).Select(b => b.ToString("X2")));
        }
    }
}
