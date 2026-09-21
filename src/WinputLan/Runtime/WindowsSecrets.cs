using System;
using System.Security.Cryptography;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        private static readonly byte[] Entropy = new byte[] { 0x57, 0x49, 0x4E, 0x50, 0x55, 0x54, 0x2D, 0x4C, 0x41, 0x4E };

        public byte[] Protect(byte[] plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException("plaintext");
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData == null) throw new ArgumentNullException("protectedData");
            return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        }
    }
}
