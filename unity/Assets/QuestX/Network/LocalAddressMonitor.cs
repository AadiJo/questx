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

    /// <summary>
    /// Tracks the headset's local IPv4 address without blocking the caller.
    /// Lookups run on the thread pool with at most one in flight; results are applied and
    /// <see cref="Changed"/> is raised only from <see cref="Poll"/>, on the caller's thread.
    /// </summary>
    /// <remarks>
    /// Selection: the routing table's preferred address wins when known. Otherwise the
    /// current address is kept while the host still has it, so reordered results never
    /// switch addresses; a new address is the lowest usable one, link-local last.
    /// A lookup that throws keeps the current address and retries at the next refresh;
    /// a successful lookup without a usable address clears it.
    /// </remarks>
    public sealed class LocalAddressMonitor
    {
        /// <summary>Legacy "no address" value of <see cref="INetworkTableConnection.IpAddress"/>.</summary>
        public const string NoAddress = "127.0.0.1";

        /// <summary>How long to wait after a lookup completes before starting the next.</summary>
        public const double DefaultRefreshSeconds = 5.0;

        private readonly Func<LocalAddressLookup> lookup;
        private readonly Func<double> clock;
        private readonly double refreshSeconds;
        private Task<LocalAddressLookup> inFlight;
        private double nextLookupAt;
        private bool lookupFailing;

        /// <param name="lookup">Blocking lookup, run off the calling thread</param>
        /// <param name="clock">Monotonic time in seconds</param>
        public LocalAddressMonitor(
            Func<LocalAddressLookup> lookup = null,
            Func<double> clock = null,
            double refreshSeconds = DefaultRefreshSeconds
        )
        {
            this.lookup = lookup ?? LookupSystemAddresses;
            if (clock == null)
            {
                var stopwatch = Stopwatch.StartNew();
                clock = () => stopwatch.Elapsed.TotalSeconds;
            }
            this.clock = clock;
            this.refreshSeconds = refreshSeconds;
            nextLookupAt = double.NegativeInfinity;
        }

        /// <summary>
        /// The selected IPv4 address, or null when none is known. Never loopback or unspecified.
        /// </summary>
        public string Address { get; private set; }

        /// <summary>
        /// Raised from <see cref="Poll"/> when <see cref="Address"/> changes; null means lost.
        /// </summary>
        public event Action<string> Changed;

        /// <summary>The running lookup, if any. Exposed for checks.</summary>
        internal Task InFlightLookup => inFlight;

        /// <summary>
        /// Applies a finished lookup and starts the next one when due. Never waits for a lookup.
        /// </summary>
        public void Poll()
        {
            if (inFlight != null)
            {
                if (!inFlight.IsCompleted)
                    return;
                var finished = inFlight;
                inFlight = null;
                nextLookupAt = clock() + refreshSeconds;
                Apply(finished);
            }

            if (clock() >= nextLookupAt)
            {
                inFlight = Task.Run(lookup);
            }
        }

        private void Apply(Task<LocalAddressLookup> finished)
        {
            if (finished.Status != TaskStatus.RanToCompletion)
            {
                if (!lookupFailing)
                {
                    var error = finished.Exception?.GetBaseException();
                    QueuedLogger.LogWarning(
                        $"Local address lookup failed; keeping {Address ?? "no address"}: {error?.Message ?? "canceled"}"
                    );
                }
                lookupFailing = true;
                return;
            }
            lookupFailing = false;

            var result = finished.Result;
            var selected = Select(Address, result.Preferred, result.Candidates);
            string next = selected?.ToString();
            if (next == Address)
                return;

            Address = next;
            Changed?.Invoke(next);
        }

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
