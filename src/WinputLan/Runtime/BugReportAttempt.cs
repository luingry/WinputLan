using System;
using System.Security.Cryptography;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    // Lives only as long as the report window. Frozen retries preserve receipt identity.
    public sealed class BugReportAttempt
    {
        public BugReport Report { get; private set; }
        public string Secret { get; private set; }
        public bool Frozen { get; set; }
        public static BugReportAttempt Create(BugReport report)
        {
            var random = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            return new BugReportAttempt { Report = report, Secret = Convert.ToBase64String(random).TrimEnd('=').Replace('+','-').Replace('/','_') };
        }
    }
}
