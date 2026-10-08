using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WinputLan.Core
{
    public sealed class ReleaseNotesSection
    {
        public ReleaseNotesSection(string heading) { Heading = heading; Items = new List<string>(); }
        public string Heading { get; private set; }
        public List<string> Items { get; private set; }
    }

    /// <summary>Extracts one version's entry from a Keep a Changelog document.</summary>
    public static class ReleaseNotes
    {
        public static IList<ReleaseNotesSection> Parse(string changelog, string version)
        {
            var sections = new List<ReleaseNotesSection>();
            if (string.IsNullOrEmpty(changelog) || string.IsNullOrWhiteSpace(version)) return sections;
            var match = Regex.Match(changelog.Replace("\r\n", "\n"), "(?ms)^## \\[" + Regex.Escape(version.Trim()) + "\\][^\n]*\n(.*?)(?=^## \\[|\\z)");
            if (!match.Success) return sections;
            ReleaseNotesSection current = null;
            foreach (var raw in match.Groups[1].Value.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                if (line.StartsWith("### ", StringComparison.Ordinal)) { current = new ReleaseNotesSection(line.Substring(4).Trim()); sections.Add(current); continue; }
                if (current == null) { current = new ReleaseNotesSection(""); sections.Add(current); }
                var bullet = Regex.Match(line, "^\\s*[-*]\\s+(.*)$");
                if (bullet.Success) current.Items.Add(bullet.Groups[1].Value.Trim());
                else if (current.Items.Count > 0) current.Items[current.Items.Count - 1] += " " + line.Trim();
                else current.Items.Add(line.Trim());
            }
            sections.RemoveAll(s => s.Items.Count == 0);
            return sections;
        }

        // True on the first run of a version newer than the last one that ran. A brand-new install
        // (fresh config) skips it; an existing config without a recorded version predates this marker.
        public static bool ShouldShowAfterUpdate(string lastSeenVersion, string installedVersion, bool freshConfig)
        {
            Version installed, last;
            if (freshConfig || !Version.TryParse(installedVersion, out installed)) return false;
            if (string.IsNullOrWhiteSpace(lastSeenVersion) || !Version.TryParse(lastSeenVersion, out last)) return true;
            return installed > last;
        }
    }
}
