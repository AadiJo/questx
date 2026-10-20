using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuestX.Config;
using QuestX.Core;
using QuestX.Utils;
using UnityEngine;

namespace QuestX.Network
{
    /// <summary>
    /// Interface for the mDNS hostname responder.
    /// </summary>
    public interface IMdnsResponder : IDisposable
    {
        /// <summary>
        /// Gets the hostnames currently being answered, e.g. quest.local and quest-1234.local
        /// </summary>
        IReadOnlyList<string> Hostnames { get; }

        /// <summary>
        /// Opens the mDNS socket and starts answering queries.
        /// </summary>
        void Start();

        /// <summary>
        /// Updates the address that hostnames resolve to. Called from QuestX.SlowUpdate() at 3Hz.
        /// </summary>
        /// <param name="ipAddress">The headset's IPv4 address, or null when it has none</param>
        void Periodic(string ipAddress);
    }

    /// <summary>
    /// Minimal multicast DNS responder (RFC 6762) that answers IPv4 address queries for
    /// quest.local and quest-TEAM.local, so users can reach the web dashboard without
    /// knowing the headset's IP. Only A records are served; no services are advertised.
    /// </summary>
    public sealed class MdnsResponder : IMdnsResponder
    {
        private const ushort TYPE_A = 1;
        private const ushort TYPE_AAAA = 28;
        private const ushort TYPE_NSEC = 47;
        private const ushort TYPE_ANY = 255;
        private const ushort CLASS_IN = 1;
        private const ushort CLASS_ANY = 255;
        private const ushort CLASS_CACHE_FLUSH = 0x8000;
        private const ushort FLAGS_RESPONSE = 0x8400; // QR + AA
        private const int MAX_NAME_POINTER_JUMPS = 16;

        /// <summary>
        /// Hostnames and address to answer with. Replaced as a whole so the receive thread
        /// always sees a consistent pair.
        /// </summary>
        private sealed class Snapshot
        {
            public string[] Hostnames;
            public IPAddress Address;
        }

        private readonly IConfigManager configManager;
        private readonly IPAddress multicastGroup = IPAddress.Parse(
            QuestXConstants.Mdns.MULTICAST_ADDRESS
        );
        private readonly IPEndPoint multicastEndPoint;
        private readonly object socketLock = new();

        private volatile Snapshot snapshot;
        private volatile bool running;
        private UdpClient client;
        private Thread receiveThread;
        private IPAddress joinedInterface;
        private int teamNumber = QuestXConstants.Network.TEAM_NUMBER_DISABLED;

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Wi-Fi drops multicast packets unless a lock is held. Not needed on Ethernet,
        /// but harmless there and keeps quest.local working on a Wi-Fi bench setup.
        /// </summary>
        private AndroidJavaObject multicastLock;
#endif

        public MdnsResponder(IConfigManager configManager)
        {
            this.configManager = configManager;
            multicastEndPoint = new IPEndPoint(multicastGroup, QuestXConstants.Mdns.PORT);
            snapshot = new Snapshot { Hostnames = BuildHostnames(teamNumber), Address = null };

            configManager.OnTeamNumberChanged += OnTeamNumberChanged;
        }

        #region Properties
        public IReadOnlyList<string> Hostnames => snapshot.Hostnames;
        #endregion

        #region Event Subscribers
        private void OnTeamNumberChanged(int newTeamNumber)
        {
            teamNumber = newTeamNumber;
            var current = snapshot;
            snapshot = new Snapshot
            {
                Hostnames = BuildHostnames(teamNumber),
                Address = current.Address,
            };
            Announce();
        }
        #endregion

        #region Lifecycle
        public void Start()
        {
            if (running)
                return;

            AcquireMulticastLock();

            try
            {
                client = new UdpClient(AddressFamily.InterNetwork);
                // Android's own mDNS stack may also hold port 5353
                client.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    true
                );
                client.Client.Bind(new IPEndPoint(IPAddress.Any, QuestXConstants.Mdns.PORT));
                client.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastTimeToLive,
                    255
                );
                client.Client.ReceiveTimeout = 1000;
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError($"Failed to open mDNS socket: {ex.Message}");
                client?.Close();
                client = null;
                ReleaseMulticastLock();
                return;
            }

            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "QuestX mDNS" };
            receiveThread.Start();

            QueuedLogger.Log($"mDNS responder started for {string.Join(", ", Hostnames)}");
        }

        public void Periodic(string ipAddress)
        {
            if (!running)
                return;

            IPAddress address = null;
            if (!string.IsNullOrEmpty(ipAddress) && IPAddress.TryParse(ipAddress, out var parsed))
            {
                address = parsed;
            }

            var current = snapshot;
            if (Equals(address, current.Address))
                return;

            JoinGroup(address);
            snapshot = new Snapshot { Hostnames = current.Hostnames, Address = address };
            if (address != null)
            {
                QueuedLogger.Log(
                    $"mDNS: {string.Join(", ", current.Hostnames)} now resolve to {address}"
                );
            }
            Announce();
        }

        public void Dispose()
        {
            configManager.OnTeamNumberChanged -= OnTeamNumberChanged;

            running = false;
            lock (socketLock)
            {
                client?.Close();
                client = null;
                joinedInterface = null;
            }
            receiveThread?.Join(2000);
            receiveThread = null;

            ReleaseMulticastLock();
        }
        #endregion

        #region Socket Management
        /// <summary>
        /// Moves the multicast group membership and outgoing interface to the given address.
        /// </summary>
        private void JoinGroup(IPAddress address)
        {
            lock (socketLock)
            {
                if (client == null)
                    return;

                if (joinedInterface != null)
                {
                    try
                    {
                        client.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.DropMembership,
                            new MulticastOption(multicastGroup, joinedInterface)
                        );
                    }
                    catch (SocketException)
                    {
                        // The interface may already be gone
                    }
                    joinedInterface = null;
                }

                if (address == null)
                    return;

                try
                {
                    client.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(multicastGroup, address)
                    );
                    client.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        address.GetAddressBytes()
                    );
                    joinedInterface = address;
                }
                catch (SocketException ex)
                {
                    QueuedLogger.LogWarning(
                        $"mDNS: failed to join multicast group on {address}: {ex.Message}"
                    );
                }
            }
        }

        private void Send(byte[] packet, IPEndPoint destination)
        {
            lock (socketLock)
            {
                if (client == null)
                    return;
                try
                {
                    client.Send(packet, packet.Length, destination);
                }
                catch (SocketException ex)
                {
                    QueuedLogger.LogWarning($"mDNS: send failed: {ex.Message}");
                }
            }
        }

        private void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                byte[] packet;
                try
                {
                    packet = client?.Receive(ref remote);
                }
                catch (SocketException ex)
                    when (ex.SocketErrorCode == SocketError.TimedOut
                        || ex.SocketErrorCode == SocketError.WouldBlock
                    )
                {
                    continue;
                }
                catch (Exception) when (!running)
                {
                    return;
                }
                catch (Exception ex)
                {
                    QueuedLogger.LogWarning($"mDNS: receive failed: {ex.Message}");
                    Thread.Sleep(1000);
                    continue;
                }

                if (packet == null)
                    return;

                try
                {
                    HandleQuery(packet, remote);
                }
                catch (Exception ex)
                {
                    // Malformed packets must never take the responder down
                    QueuedLogger.LogWarning($"mDNS: failed to handle packet: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Sends an unsolicited response for every hostname, twice one second apart (RFC 6762
        /// section 8.3), so caches pick up a new address immediately.
        /// </summary>
        private void Announce()
        {
            var current = snapshot;
            if (!running || current.Address == null)
                return;

            byte[] packet = BuildResponse(0, current.Hostnames, current.Address, null);
            Send(packet, multicastEndPoint);
            Task.Delay(1000)
                .ContinueWith(_ =>
                {
                    if (snapshot == current)
                        Send(packet, multicastEndPoint);
                });
        }
        #endregion

        #region DNS Packet Handling
        private void HandleQuery(byte[] packet, IPEndPoint remote)
        {
            var current = snapshot;
            if (current.Address == null || packet.Length < 12)
                return;

            ushort id = ReadUInt16(packet, 0);
            ushort flags = ReadUInt16(packet, 2);
            ushort questionCount = ReadUInt16(packet, 4);

            // Ignore responses (including our own looped-back announcements) and non-standard queries
            if ((flags & 0x8000) != 0 || ((flags >> 11) & 0xF) != 0)
                return;

            var questions = new List<(string Hostname, ushort Type)>();
            var matched = new List<string>();
            int offset = 12;
            for (int i = 0; i < questionCount; i++)
            {
                string name = ReadName(packet, ref offset);
                if (name == null || offset + 4 > packet.Length)
                    return;
                ushort type = ReadUInt16(packet, offset);
                ushort qclass = (ushort)(ReadUInt16(packet, offset + 2) & ~CLASS_CACHE_FLUSH);
                offset += 4;

                if (type != TYPE_A && type != TYPE_AAAA && type != TYPE_ANY)
                    continue;
                if (qclass != CLASS_IN && qclass != CLASS_ANY)
                    continue;

                foreach (string hostname in current.Hostnames)
                {
                    if (!string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase))
                        continue;
                    questions.Add((hostname, type));
                    if (!matched.Contains(hostname))
                        matched.Add(hostname);
                }
            }

            if (matched.Count == 0)
                return;

            // Queries not sent from port 5353 come from simple resolvers that expect a
            // conventional unicast DNS reply (RFC 6762 section 6.7)
            bool legacyUnicast = remote.Port != QuestXConstants.Mdns.PORT;
            byte[] response = BuildResponse(
                legacyUnicast ? id : (ushort)0,
                matched,
                current.Address,
                legacyUnicast ? questions : null
            );
            Send(response, legacyUnicast ? remote : multicastEndPoint);
        }

        /// <summary>
        /// Builds a response with an A record and a matching NSEC record for each hostname.
        /// The NSEC record tells IPv6 lookups there is no AAAA address, so clients connect
        /// over IPv4 immediately instead of waiting for the AAAA query to time out
        /// (RFC 6762 section 6.1).
        /// </summary>
        /// <param name="questions">Questions to echo for a legacy unicast reply, or null for a multicast reply</param>
        private static byte[] BuildResponse(
            ushort id,
            IReadOnlyList<string> hostnames,
            IPAddress address,
            IReadOnlyList<(string Hostname, ushort Type)> questions
        )
        {
            bool legacyUnicast = questions != null;
            var buffer = new List<byte>(128 * hostnames.Count);
            WriteUInt16(buffer, id);
            WriteUInt16(buffer, FLAGS_RESPONSE);
            WriteUInt16(buffer, (ushort)(questions?.Count ?? 0));
            WriteUInt16(buffer, (ushort)(hostnames.Count * 2));
            WriteUInt16(buffer, 0);
            WriteUInt16(buffer, 0);

            // Legacy unicast replies must echo the question section
            if (legacyUnicast)
            {
                foreach (var question in questions)
                {
                    WriteName(buffer, question.Hostname);
                    WriteUInt16(buffer, question.Type);
                    WriteUInt16(buffer, CLASS_IN);
                }
            }

            byte[] addressBytes = address.GetAddressBytes();
            uint ttl = legacyUnicast
                ? QuestXConstants.Mdns.LEGACY_UNICAST_TTL_SECONDS
                : QuestXConstants.Mdns.TTL_SECONDS;
            // We are the only owner of these names, so tell caches to flush stale records
            ushort recordClass = legacyUnicast ? CLASS_IN : (ushort)(CLASS_IN | CLASS_CACHE_FLUSH);
            foreach (string hostname in hostnames)
            {
                WriteRecordHeader(buffer, hostname, TYPE_A, recordClass, ttl);
                WriteUInt16(buffer, (ushort)addressBytes.Length);
                buffer.AddRange(addressBytes);

                // NSEC: next name is the hostname itself; type bitmap window 0 with only A set
                WriteRecordHeader(buffer, hostname, TYPE_NSEC, recordClass, ttl);
                int nameLength = Encoding.UTF8.GetByteCount(hostname) + 2;
                WriteUInt16(buffer, (ushort)(nameLength + 3));
                WriteName(buffer, hostname);
                buffer.Add(0); // window block 0
                buffer.Add(1); // bitmap length
                buffer.Add(0x40); // bit for type 1 (A)
            }

            return buffer.ToArray();
        }

        private static void WriteRecordHeader(
            List<byte> buffer,
            string hostname,
            ushort type,
            ushort recordClass,
            uint ttl
        )
        {
            WriteName(buffer, hostname);
            WriteUInt16(buffer, type);
            WriteUInt16(buffer, recordClass);
            WriteUInt16(buffer, (ushort)(ttl >> 16));
            WriteUInt16(buffer, (ushort)ttl);
        }

        /// <summary>
        /// Reads a possibly-compressed DNS name and advances offset past it.
        /// </summary>
        /// <returns>The dotted name, or null if the name is malformed</returns>
        private static string ReadName(byte[] packet, ref int offset)
        {
            var name = new StringBuilder();
            int position = offset;
            int jumps = 0;
            bool jumped = false;

            while (true)
            {
                if (position >= packet.Length)
                    return null;

                byte length = packet[position];
                if (length == 0)
                {
                    position++;
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (position + 1 >= packet.Length || ++jumps > MAX_NAME_POINTER_JUMPS)
                        return null;
                    int pointer = ((length & 0x3F) << 8) | packet[position + 1];
                    if (!jumped)
                        offset = position + 2;
                    jumped = true;
                    position = pointer;
                    continue;
                }

                if ((length & 0xC0) != 0 || position + 1 + length > packet.Length)
                    return null;

                if (name.Length > 0)
                    name.Append('.');
                name.Append(Encoding.UTF8.GetString(packet, position + 1, length));
                position += 1 + length;
            }

            if (!jumped)
                offset = position;
            return name.ToString();
        }

        private static void WriteName(List<byte> buffer, string name)
        {
            foreach (string label in name.Split('.'))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                buffer.Add((byte)bytes.Length);
                buffer.AddRange(bytes);
            }
            buffer.Add(0);
        }

        private static ushort ReadUInt16(byte[] packet, int offset) =>
            (ushort)((packet[offset] << 8) | packet[offset + 1]);

        private static void WriteUInt16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
        #endregion

        #region Utility Methods
        private static string[] BuildHostnames(int teamNumber)
        {
            string hostname = QuestXConstants.Mdns.HOSTNAME;
            return teamNumber > 0
                ? new[] { $"{hostname}.local", $"{hostname}-{teamNumber}.local" }
                : new[] { $"{hostname}.local" };
        }

        private void AcquireMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var context = activity.Call<AndroidJavaObject>("getApplicationContext");
                using var wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi");
                multicastLock = wifiManager.Call<AndroidJavaObject>(
                    "createMulticastLock",
                    "questx-mdns"
                );
                multicastLock.Call("setReferenceCounted", false);
                multicastLock.Call("acquire");
            }
            catch (Exception ex)
            {
                QueuedLogger.LogWarning($"mDNS: failed to acquire multicast lock: {ex.Message}");
                multicastLock = null;
            }
#endif
        }

        private void ReleaseMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                multicastLock?.Call("release");
            }
            catch (Exception ex)
            {
                QueuedLogger.LogWarning($"mDNS: failed to release multicast lock: {ex.Message}");
            }
            multicastLock?.Dispose();
            multicastLock = null;
#endif
        }
        #endregion
    }
}
