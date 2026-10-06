using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    [DataContract]
    public sealed class BugReportDraft
    {
        [DataMember] public BugReport Report { get; set; }
        [DataMember] public string Secret { get; set; }
        [DataMember] public bool Frozen { get; set; }
        [DataMember] public DateTime SavedUtc { get; set; }
        public static BugReportDraft Create(BugReport report)
        {
            var random = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            return new BugReportDraft { Report = report, Secret = Convert.ToBase64String(random).TrimEnd('=').Replace('+','-').Replace('/','_') };
        }
    }
    public sealed class BugReportDraftStore
    {
        private readonly string _path;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinputLan-bug-report-v1");
        public BugReportDraftStore(string directory) { Directory.CreateDirectory(directory); _path = Path.Combine(directory, "report-draft.dat"); }
        public BugReportDraft Load()
        {
            try {
                if (!File.Exists(_path) || new FileInfo(_path).Length > BugReportJson.MaximumBytes + 2048) return null;
                var draft = BugReportJson.Deserialize<BugReportDraft>(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser));
                if (draft.SavedUtc < DateTime.UtcNow.AddDays(-30) || draft.SavedUtc > DateTime.UtcNow.AddDays(1) || draft.Report == null) { Clear(); return null; }
                return draft;
            } catch { return null; }
        }
        public void Save(BugReportDraft draft)
        {
            draft.SavedUtc = DateTime.UtcNow;
            var bytes = ProtectedData.Protect(BugReportJson.Serialize(draft), Entropy, DataProtectionScope.CurrentUser);
            var temporary = _path + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Clear() { if (File.Exists(_path)) File.Delete(_path); }
    }
}
