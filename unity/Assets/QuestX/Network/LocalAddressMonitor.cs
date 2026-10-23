using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using QuestX.Utils;

namespace QuestX.Network
{
    /// <summary>
    /// Result of one local address lookup.
    /// </summary>
    public readonly struct LocalAddressLookup
    {
        /// <summary>Source address the routing table picks for outbound traffic, or null.</summary>
        public readonly IPAddress Preferred;

        /// <summary>Addresses assigned to this host, in any order.</summary>
        public readonly IReadOnlyList<IPAddress> Candidates;

        public LocalAddressLookup(IPAddress preferred, IReadOnlyList<IPAddress> candidates)
        {
            Preferred = preferred;
            Candidates = candidates ?? Array.Empty<IPAddress>();
        }
    }

    public sealed class LocalAddressMonitor
    {
        /// <summary>
        /// Chooses the address to report. Deterministic for a given input set.
        /// </summary>
        internal static IPAddress Select(
            string current,
            IPAddress preferred,
            IReadOnlyList<IPAddress> candidates
        )
        {
            if (IsUsable(preferred))
                return preferred;

            IPAddress best = null;
            foreach (var address in candidates)
            {
                if (!IsUsable(address))
                    continue;
                if (current != null && address.ToString() == current)
                    return address;
                if (best == null || Compare(address, best) < 0)
                    best = address;
            }
            return best;
        }

        /// <summary>True for an IPv4 address other than loopback, unspecified, or broadcast.</summary>
        internal static bool IsUsable(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] b = address.GetAddressBytes();
            return b[0] != 0
                && b[0] != 127
                && !(b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255);
        }

        private static bool IsLinkLocal(byte[] b) => b[0] == 169 && b[1] == 254;

        private static int Compare(IPAddress x, IPAddress y)
        {
            byte[] a = x.GetAddressBytes();
            byte[] b = y.GetAddressBytes();
            int linkLocal = IsLinkLocal(a).CompareTo(IsLinkLocal(b));
            if (linkLocal != 0)
                return linkLocal;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            }
            return 0;
        }

        private static LocalAddressLookup LookupSystemAddresses() =>
            Lookup(RouteSourceAddress, InterfaceAddresses, HostNameAddresses);

        /// <summary>
        /// Lookup policy. A usable route source address is returned without consulting the
        /// slower sources. Otherwise up interfaces are enumerated, then the host name is
        /// resolved if enumeration is unsupported. "No address" is reported only when the
        /// route answered "unreachable" and an address source answered with nothing usable;
        /// any other outcome without an address throws, so the current address is kept.
        /// </summary>
        /// <param name="route">Route source address, or null when there is no route. Throws on other errors.</param>
        /// <param name="interfaces">IPv4 addresses of up interfaces. Throws when unsupported.</param>
        /// <param name="hostName">Addresses of the host name. Throws on resolver errors.</param>
        internal static LocalAddressLookup Lookup(
            Func<IPAddress> route,
            Func<IReadOnlyList<IPAddress>> interfaces,
            Func<IReadOnlyList<IPAddress>> hostName
        )
        {
            IPAddress preferred = null;
            Exception routeError = null;
            try
            {
                preferred = route();
                if (IsUsable(preferred))
                    return new LocalAddressLookup(preferred, null);
            }
            catch (Exception e)
            {
                routeError = e;
            }

            IReadOnlyList<IPAddress> candidates;
            try
            {
                candidates = interfaces();
            }
            catch (Exception interfaceError)
            {
                try
                {
                    candidates = hostName();
                }
                catch (Exception hostNameError)
                {
                    throw new AggregateException(
                        "No local address source is available",
                        routeError ?? interfaceError,
                        hostNameError
                    );
                }
            }

            if (routeError != null && Select(null, null, candidates) == null)
                throw new AggregateException(
                    "Route lookup failed and no address was found",
                    routeError
                );
            return new LocalAddressLookup(preferred, candidates);
        }

        /// <summary>
        /// Source address of the default IPv4 route, or null when there is no route.
        /// No packets are sent.
        /// </summary>
        private static IPAddress RouteSourceAddress()
        {
            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp
                );
                // Connecting a UDP socket only selects a route; no network traffic is sent.
                socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException e) when (IsNoRoute(e.SocketErrorCode))
            {
                return null;
            }
        }

        private static bool IsNoRoute(SocketError error) =>
            error == SocketError.NetworkUnreachable
            || error == SocketError.HostUnreachable
            || error == SocketError.NetworkDown
            || error == SocketError.AddressNotAvailable;

        private static IReadOnlyList<IPAddress> InterfaceAddresses()
        {
            var addresses = new List<IPAddress>();
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    addresses.Add(unicast.Address);
            }
            return addresses;
        }

        private static IReadOnlyList<IPAddress> HostNameAddresses() =>
            Dns.GetHostAddresses(Dns.GetHostName());
    }
}
