using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace WinputLan.Core
{
    public sealed class LanAddressCandidate
    {
        public string Address { get; set; }
        public bool InterfaceUp { get; set; }
        public bool HasGateway { get; set; }
        public bool IsEthernetOrWifi { get; set; }
    }
    public static class LanAddressSelector
    {
        public static string Select(IEnumerable<LanAddressCandidate> candidates)
        {
            var valid = (candidates ?? Enumerable.Empty<LanAddressCandidate>()).Where(c => c != null && c.InterfaceUp && IsUsable(c.Address)).ToArray();
            return valid.Where(c => c.HasGateway && c.IsEthernetOrWifi).Select(c => c.Address).FirstOrDefault()
                ?? valid.Where(c => c.HasGateway).Select(c => c.Address).FirstOrDefault()
                ?? valid.Where(c => c.IsEthernetOrWifi).Select(c => c.Address).FirstOrDefault()
                ?? valid.Select(c => c.Address).FirstOrDefault();
        }
        private static bool IsUsable(string value)
        {
            IPAddress address;
            if (!IPAddress.TryParse(value, out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
            var bytes = address.GetAddressBytes(); return !(bytes[0] == 169 && bytes[1] == 254);
        }
    }
}
