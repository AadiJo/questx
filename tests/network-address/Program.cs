using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using QuestX.Network;

namespace QuestX.Utils
{
    public static class QueuedLogger
    {
        public static readonly ConcurrentQueue<string> Warnings = new ConcurrentQueue<string>();
        public static void LogWarning(string message) => Warnings.Enqueue(message);
    }
}

static class Program
{
    static int checks;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); checks++; }

    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static LocalAddressLookup Hosts(IPAddress preferred, params string[] candidates) =>
        new LocalAddressLookup(preferred, Array.ConvertAll(candidates, Ip));

    /// <summary>Scripted resolver: each call takes the next result, optionally gated.</summary>
    sealed class Resolver
    {
        public readonly Queue<Func<LocalAddressLookup>> Script = new Queue<Func<LocalAddressLookup>>();
        public int Calls, Active, MaxActive, ThreadId = -1;
        public ManualResetEventSlim Gate = new ManualResetEventSlim(true);

        public LocalAddressLookup Lookup()
        {
            Interlocked.Increment(ref Calls);
            int active = Interlocked.Increment(ref Active);
            if (active > MaxActive) MaxActive = active;
            ThreadId = Environment.CurrentManagedThreadId;
            try
            {
                Gate.Wait();
                return Script.Dequeue()();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    sealed class Clock { public double Now; public double Read() => Now; }

    sealed class Harness
    {
        public readonly Resolver Resolver = new Resolver();
        public readonly Clock Clock = new Clock();
        public readonly LocalAddressMonitor Monitor;
        public readonly List<string> Changes = new List<string>();
        public readonly List<int> ChangeThreads = new List<int>();

        public Harness()
        {
            Monitor = new LocalAddressMonitor(Resolver.Lookup, Clock.Read, refreshSeconds: 5);
            Monitor.Changed += a => { Changes.Add(a ?? "none"); ChangeThreads.Add(Environment.CurrentManagedThreadId); };
        }

        public void Next(LocalAddressLookup result) => Resolver.Script.Enqueue(() => result);
        public void NextThrows(Exception e) => Resolver.Script.Enqueue(() => throw e);

        /// <summary>Starts a lookup if due, waits for it off this thread, then applies it.</summary>
        public void Cycle()
        {
            Monitor.Poll();
            var task = Monitor.InFlightLookup;
            if (task == null) throw new Exception("no lookup started");
            if (!SpinWait.SpinUntil(() => task.IsCompleted, 2000)) throw new Exception("lookup hung");
            Monitor.Poll();
            Clock.Now += 5;
        }

        public string History => string.Join(" -> ", Changes);
    }

    static void Main()
    {
        // Reordered multi-address results never alternate (the audit's A -> B -> A -> B repro).
        var h = new Harness();
        h.Next(Hosts(null, "192.168.1.2", "10.0.0.2"));
        h.Next(Hosts(null, "192.168.1.2", "10.0.0.2"));
        h.Next(Hosts(null, "10.0.0.2", "192.168.1.2"));
        h.Next(Hosts(null, "192.168.1.2", "10.0.0.2"));
        for (int i = 0; i < 4; i++) h.Cycle();
        Check(h.History == "10.0.0.2", "stable fallback is chosen once: " + h.History);
        Check(h.Monitor.Address == "10.0.0.2", "address kept across reordering");

        // An existing address is retained even when a lower one appears.
        h.Next(Hosts(null, "10.0.0.1", "192.168.1.2", "10.0.0.2"));
        h.Cycle();
        Check(h.History == "10.0.0.2", "current address retained while valid: " + h.History);

        // The route's preferred source address wins; loopback, unspecified, IPv6 and broadcast are ignored.
        h = new Harness();
        h.Next(Hosts(null, "127.0.0.1", "0.0.0.0", "255.255.255.255", "::1", "fe80::1"));
        h.Cycle();
        Check(h.Changes.Count == 0 && h.Monitor.Address == null, "only unusable addresses report none");
        h.Next(Hosts(Ip("127.0.0.1"), "169.254.3.4", "172.22.11.2"));
        h.Cycle();
        Check(h.History == "172.22.11.2", "loopback route ignored, link-local last: " + h.History);
        h.Next(Hosts(Ip("10.12.34.5"), "172.22.11.2"));
        h.Cycle();
        h.Next(Hosts(Ip("10.12.34.5"), "172.22.11.2", "10.12.34.5"));
        h.Cycle();
        Check(h.History == "172.22.11.2 -> 10.12.34.5", "route change moves once: " + h.History);
        // Route temporarily unknown: the current address stays while the host still has it.
        h.Next(Hosts(null, "172.22.11.2", "10.12.34.5"));
        h.Cycle();
        Check(h.Monitor.Address == "10.12.34.5", "route loss keeps a still-assigned address");

        // Errors keep the last address, log once per failure streak, and recover.
        h = new Harness();
        h.Next(Hosts(Ip("10.0.0.5")));
        h.Cycle();
        int warningsBefore = QuestX.Utils.QueuedLogger.Warnings.Count;
        h.NextThrows(new SocketException((int)SocketError.HostNotFound));
        h.NextThrows(new InvalidOperationException("resolver broke"));
        h.Cycle();
        h.Cycle();
        Check(h.Monitor.Address == "10.0.0.5", "lookup errors keep the last address");
        Check(QuestX.Utils.QueuedLogger.Warnings.Count == warningsBefore + 1, "failure streak logged once");
        h.Next(Hosts(Ip("10.0.0.6")));
        h.Cycle();
        Check(h.History == "10.0.0.5 -> 10.0.0.6", "recovers after errors: " + h.History);

        // Genuine loss and reappearance raise transitions on the polling thread.
        h.Next(Hosts(null));
        h.Cycle();
        h.Next(Hosts(null));
        h.Cycle();
        h.Next(Hosts(Ip("10.0.0.6")));
        h.Cycle();
        Check(h.History == "10.0.0.5 -> 10.0.0.6 -> none -> 10.0.0.6", "loss then reappearance: " + h.History);
        int pollingThread = Environment.CurrentManagedThreadId;
        Check(h.ChangeThreads.TrueForAll(t => t == pollingThread), "Changed raised only from Poll");
        Check(h.Resolver.ThreadId != pollingThread, "lookup runs off the polling thread");

        // A slow lookup never blocks Poll, never overlaps, and does not restart before refresh.
        h = new Harness();
        h.Resolver.Gate = new ManualResetEventSlim(false);
        h.Next(Hosts(Ip("10.0.0.7")));
        h.Monitor.Poll();
        var slow = h.Monitor.InFlightLookup;
        SpinWait.SpinUntil(() => h.Resolver.Active == 1, 2000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            h.Clock.Now += 1;
            h.Monitor.Poll();
        }
        watch.Stop();
        Check(watch.ElapsedMilliseconds < 500, "Poll does not wait for a slow lookup");
        Check(h.Resolver.Calls == 1 && ReferenceEquals(slow, h.Monitor.InFlightLookup), "one lookup in flight");
        Check(h.Monitor.Address == null && h.Changes.Count == 0, "no address before the lookup finishes");
        h.Resolver.Gate.Set();
        SpinWait.SpinUntil(() => slow.IsCompleted, 2000);
        Check(h.Changes.Count == 0, "results wait for Poll");
        h.Monitor.Poll();
        Check(h.History == "10.0.0.7" && h.Monitor.InFlightLookup == null, "applied without an immediate restart");
        h.Clock.Now += 4.9;
        h.Monitor.Poll();
        Check(h.Monitor.InFlightLookup == null, "next lookup waits for the refresh interval");
        h.Next(Hosts(Ip("10.0.0.7")));
        h.Clock.Now += 0.1;
        h.Monitor.Poll();
        Check(h.Monitor.InFlightLookup != null, "next lookup starts after the refresh interval");
        Check(h.Resolver.MaxActive == 1, "lookups never overlap");

        // Selection is deterministic regardless of input order.
        var forward = LocalAddressMonitor.Select(null, null, new[] { Ip("192.168.0.9"), Ip("10.1.1.1"), Ip("169.254.0.1") });
        var reverse = LocalAddressMonitor.Select(null, null, new[] { Ip("169.254.0.1"), Ip("10.1.1.1"), Ip("192.168.0.9") });
        Check(forward.Equals(Ip("10.1.1.1")) && reverse.Equals(forward), "order-independent fallback");

        CheckLookupPolicy();

        Console.WriteLine($"network address checks passed ({checks} checks)");
        }

    static void CheckLookupPolicy()
    {
        int interfaceCalls = 0, hostNameCalls = 0;
        Func<IReadOnlyList<IPAddress>> Interfaces(params string[] a) =>
            () => { interfaceCalls++; return Array.ConvertAll(a, Ip); };
        Func<IReadOnlyList<IPAddress>> HostName(params string[] a) =>
            () => { hostNameCalls++; return Array.ConvertAll(a, Ip); };
        Func<IReadOnlyList<IPAddress>> Fails(SocketError e) => () => throw new SocketException((int)e);
        Func<IReadOnlyList<IPAddress>> Unsupported = () => { interfaceCalls++; throw new PlatformNotSupportedException(); };
        Func<IPAddress> Route(string a) => () => a == null ? null : Ip(a);
        Func<IPAddress> RouteFails = () => throw new SocketException((int)SocketError.AccessDenied);
        bool Throws(Action a) { try { a(); return false; } catch (AggregateException) { return true; } }

        // A usable route answer skips the slower sources entirely.
        var r = LocalAddressMonitor.Lookup(Route("10.0.0.9"), Interfaces("10.0.0.1"), HostName("10.0.0.2"));
        Check(r.Preferred.Equals(Ip("10.0.0.9")) && interfaceCalls == 0 && hostNameCalls == 0, "route answer is not delayed by enumeration or DNS");

        // No route: interfaces supply the address; DNS is not consulted when enumeration works.
        r = LocalAddressMonitor.Lookup(Route(null), Interfaces("127.0.0.1", "172.22.11.2"), HostName("10.0.0.2"));
        Check(LocalAddressMonitor.Select(null, r.Preferred, r.Candidates).Equals(Ip("172.22.11.2")) && hostNameCalls == 0, "interfaces used without route");

        // Genuine loss: no route and an empty enumeration is a successful "none".
        r = LocalAddressMonitor.Lookup(Route(null), Interfaces("127.0.0.1"), HostName());
        Check(LocalAddressMonitor.Select(null, r.Preferred, r.Candidates) == null, "no route and no interface is none");

        // Enumeration unsupported: the host name is the fallback, and its empty answer is still "none".
        r = LocalAddressMonitor.Lookup(Route(null), Unsupported, HostName("10.0.0.2"));
        Check(r.Candidates.Count == 1 && hostNameCalls == 1, "DNS fallback when interfaces are unsupported");
        r = LocalAddressMonitor.Lookup(Route(null), Unsupported, HostName("127.0.0.1"));
        Check(LocalAddressMonitor.Select(null, r.Preferred, r.Candidates) == null, "fallback negative answer is none");

        // Errors are not "none": they throw so the monitor keeps the current address.
        Check(Throws(() => LocalAddressMonitor.Lookup(Route(null), Unsupported, Fails(SocketError.TryAgain))), "both enumerations failing throws");
        Check(Throws(() => LocalAddressMonitor.Lookup(RouteFails, Interfaces(), HostName())), "route error without addresses throws");
        r = LocalAddressMonitor.Lookup(RouteFails, Interfaces("10.0.0.3"), HostName());
        Check(r.Candidates.Count == 1, "route error with an interface address still succeeds");

        // End to end: a resolver error through the policy keeps the address; genuine loss clears it.
        var h = new Harness();
        h.Next(LocalAddressMonitor.Lookup(Route("10.0.0.9"), Interfaces(), HostName()));
        h.Cycle();
        h.Resolver.Script.Enqueue(() => LocalAddressMonitor.Lookup(Route(null), Unsupported, Fails(SocketError.TryAgain)));
        h.Cycle();
        Check(h.Monitor.Address == "10.0.0.9", "resolver error keeps the address");
        h.Resolver.Script.Enqueue(() => LocalAddressMonitor.Lookup(Route(null), Interfaces(), HostName()));
        h.Cycle();
        Check(h.History == "10.0.0.9 -> none", "genuine loss clears it: " + h.History);
    }
}
