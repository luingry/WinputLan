using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace WinputLan.Core
{
    [DataContract]
    public sealed class WinputConfig
    {
        [DataMember(Order = 1)] public string DeviceId { get; set; }
        [DataMember(Order = 2)] public string DisplayName { get; set; }
        [DataMember(Order = 3)] public int ListenPort { get; set; }
        [DataMember(Order = 4)] public string RemoteAddress { get; set; }
        [DataMember(Order = 5)] public int RemotePort { get; set; }
        [DataMember(Order = 6)] public string PinnedDeviceId { get; set; }
        [DataMember(Order = 7)] public string PinnedFingerprint { get; set; }
        [DataMember(Order = 8)] public bool RequireSignedUpdates { get; set; }
        [DataMember(Order = 9)] public string LocalHotkey { get; set; }
        [DataMember(Order = 10)] public string RemoteHotkey { get; set; }
        [DataMember(Order = 11)] public byte[] PinnedSecret { get; set; }
        [DataMember(Order = 12)] public bool ContinueInBackground { get; set; }
        // Missing in older config files, which then default to Daily (0).
        [DataMember(Order = 13)] public UpdateCheckFrequency UpdateCheckFrequency { get; set; }
        [DataMember(Order = 14)] public long LastUpdateCheckUtcTicks { get; set; }
        [DataMember(Order = 15)] public bool RunElevated { get; set; }
        // Target this PC controls without a code while its identity and certificate are unchanged.
        [DataMember(Order = 16)] public TrustedPeer TrustedTarget { get; set; }
        // Controllers this PC recognises; cleared when the user intentionally renews the access code.
        [DataMember(Order = 17)] public List<TrustedPeer> TrustedControllers { get; set; }
        [DataMember(Order = 18)] public bool StartWithWindows { get; set; }
        // Recognized controllers (valid trust proof and unchanged certificate) start without the approval prompt.
        [DataMember(Order = 19)] public bool AutoAcceptKnownConnections { get; set; }

        public static WinputConfig CreateDefault()
        {
            return new WinputConfig
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                DisplayName = Environment.MachineName,
                ListenPort = 45900,
                RemotePort = 45900,
                RequireSignedUpdates = true,
                LocalHotkey = "Ctrl+Shift+Alt+1",
                RemoteHotkey = "Ctrl+Shift+Alt+2"
            };
        }
    }

    public static class ConfigValidator
    {
        public static IReadOnlyList<string> Validate(WinputConfig config)
        {
            var errors = new List<string>();
            if (config == null) { errors.Add("Configuration is missing."); return errors; }
            Guid parsed;
            if (!Guid.TryParse(config.DeviceId, out parsed)) errors.Add("DeviceId must be a GUID.");
            if (string.IsNullOrWhiteSpace(config.DisplayName) || config.DisplayName.Length > 64 || config.DisplayName.Any(char.IsControl)) errors.Add("DisplayName must be 1-64 printable characters.");
            if (config.ListenPort < 1024 || config.ListenPort > 65535) errors.Add("ListenPort must be between 1024 and 65535.");
            if (config.RemotePort < 0 || config.RemotePort > 65535) errors.Add("RemotePort must be between 0 and 65535.");
            if (!string.IsNullOrWhiteSpace(config.RemoteAddress) && config.RemoteAddress.Any(char.IsControl)) errors.Add("RemoteAddress contains a control character.");
            if (string.IsNullOrWhiteSpace(config.PinnedDeviceId) != string.IsNullOrWhiteSpace(config.PinnedFingerprint)) errors.Add("PinnedDeviceId and PinnedFingerprint must be configured together.");
            if (!string.IsNullOrWhiteSpace(config.PinnedDeviceId) && !Guid.TryParse(config.PinnedDeviceId, out parsed)) errors.Add("PinnedDeviceId must be a GUID.");
            if (!string.IsNullOrWhiteSpace(config.PinnedFingerprint) && (config.PinnedFingerprint.Length != 64 || config.PinnedFingerprint.Any(c => !Uri.IsHexDigit(c)))) errors.Add("PinnedFingerprint must be a SHA-256 hex value.");
            if (!string.IsNullOrWhiteSpace(config.LocalHotkey)) ValidateHotkey(config.LocalHotkey, "LocalHotkey", errors);
            if (!string.IsNullOrWhiteSpace(config.RemoteHotkey)) ValidateHotkey(config.RemoteHotkey, "RemoteHotkey", errors);
            return errors;
        }

        private static void ValidateHotkey(string value, string name, List<string> errors)
        {
            try { HotkeyGesture.Parse(value); } catch (Exception) { errors.Add(name + " is invalid."); }
        }
    }
}
