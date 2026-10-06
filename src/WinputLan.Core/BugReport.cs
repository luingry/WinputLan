using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace WinputLan.Core
{
    [DataContract]
    public sealed class BugReport
    {
        [DataMember(Name = "schema")] public int Schema { get; set; } = 1;
        [DataMember(Name = "id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Name = "capturedUtc")] public string CapturedUtc { get; set; } = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        [DataMember(Name = "title")] public string Title { get; set; } = "";
        [DataMember(Name = "description")] public string Description { get; set; } = "";
        [DataMember(Name = "steps")] public string Steps { get; set; } = "";
        [DataMember(Name = "diagnostics")] public BugReportDiagnosticsData Diagnostics { get; set; } = new BugReportDiagnosticsData();
    }
    [DataContract]
    public sealed class BugReportDiagnosticsData
    {
        [DataMember(Name = "metadata")] public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        [DataMember(Name = "monitors")] public List<BugReportMonitor> Monitors { get; set; } = new List<BugReportMonitor>();
        [DataMember(Name = "events")] public List<BugReportEvent> Events { get; set; } = new List<BugReportEvent>();
    }
    [DataContract]
    public sealed class BugReportMonitor
    {
        [DataMember(Name = "x")] public int X { get; set; }
        [DataMember(Name = "y")] public int Y { get; set; }
        [DataMember(Name = "width")] public int Width { get; set; }
        [DataMember(Name = "height")] public int Height { get; set; }
        [DataMember(Name = "primary")] public bool Primary { get; set; }
    }
    [DataContract]
    public sealed class BugReportEvent
    {
        [DataMember(Name = "time")] public string Time { get; set; }
        [DataMember(Name = "origin")] public string Origin { get; set; }
        [DataMember(Name = "destination")] public string Destination { get; set; }
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
    }
    [DataContract]
    public sealed class BugReportSubmission
    {
        [DataMember(Name = "report")] public BugReport Report { get; set; }
        [DataMember(Name = "secret")] public string Secret { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
    }
    public static class BugReportJson
    {
        public const int MaximumBytes = 96 * 1024;
        public static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                Serializer(typeof(T)).WriteObject(stream, value);
                if (stream.Length > MaximumBytes) throw new InvalidOperationException("O relato excede o limite de 96 KB.");
                return stream.ToArray();
            }
        }
        public static T Deserialize<T>(byte[] bytes)
        {
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Relato muito grande.");
            using (var stream = new MemoryStream(bytes)) return (T)Serializer(typeof(T)).ReadObject(stream);
        }
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type,
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true, MaxItemsInObjectGraph = 20000 });
        public static string Preview(BugReport report) => Encoding.UTF8.GetString(Serialize(report.Diagnostics));
        public static string Validate(BugReport report)
        {
            if (report == null || report.Schema != 1 || !Regex.IsMatch(report.Id ?? "", "^[a-f0-9]{32}$")) return "Relato inválido.";
            if (string.IsNullOrWhiteSpace(report.Title) || report.Title.Trim().Length < 3 || report.Title.Length > 160) return "Informe um título com 3 a 160 caracteres.";
            if (string.IsNullOrWhiteSpace(report.Description) || report.Description.Trim().Length < 10 || report.Description.Length > 6000) return "Descreva o problema com pelo menos 10 caracteres.";
            if (report.Steps == null || report.Steps.Length > 4000) return "Os passos devem ter até 4.000 caracteres.";
            return null;
        }
    }
    public static class BugReportPrivacy
    {
        private static readonly HashSet<string> Types = new HashSet<string>(new[] { "Session", "Config", "Hooks", "Transport", "Access", "Listener", "Target", "Edge", "Input", "Pairing", "Startup", "Update", "Atalhos", "Input.MouseMove", "Input.MouseDelta", "Input.MouseWheel", "Input.MouseDown", "Input.MouseUp", "Input.KeyDown", "Input.KeyUp", "Input.ReleaseAll", "Input.Focus" });
        private static readonly HashSet<string> Statuses = new HashSet<string>(("ready ready-elevated pin-reset-safe armed-controller-only unavailable connecting failed user-disconnected reconnecting retry-pending controller-ready target-ready blocked-unpaired selected restored locked-by-controller unlocked on off auto-accepted auto-accept-failed approval-requested code-renewed code-renewed-trust-cleared request-cancelled resume-requested trust-rejected pin-save-failed controller-active blocked-by-windows dropped-sink sent received received-unfocused dropped-unfocused dropped-disconnected task run-key salvos no-update postponed validated error Offline Connecting Pairing Connected Reconnecting Faulted").Split(' '));
        public static List<BugReportEvent> Events(IEnumerable<TransactionLogEntry> entries)
        {
            return entries.Where(e => Types.Contains(e.Type)).TakeLastCompatible(500).Select(e => new BugReportEvent {
                Time = e.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), Origin = Side(e.Origin), Destination = Side(e.Destination), Type = e.Type,
                Status = Statuses.Contains(e.Status) || Regex.IsMatch(e.Status ?? "", "^(selected-edge|restored-edge|touched) [0-9]{1,3}%$") ? e.Status : "redacted"
            }).ToList();
        }
        private static string Side(string side) => side == "local" || side == "remote" ? side : "system";
        private static IEnumerable<T> TakeLastCompatible<T>(this IEnumerable<T> values, int count) { var list = values.ToList(); return list.Skip(Math.Max(0, list.Count - count)); }
    }
}
