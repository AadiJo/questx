// Portable boundary stand-ins. Production connection, telemetry, wire formats, domain
// geometry/covariance, and generated protobuf messages execute unchanged in this suite.
using Google.Protobuf;
using System.Collections.Concurrent;
using System.Globalization;

namespace QuestX.Native.NTCore
{
    public struct PubSubOptions
    {
        public static PubSubOptions AllDefault => new();
        public double Periodic;
        public bool SendAll, KeepDuplicates;
        public int PollStorage;
    }

    public record Write(string Path, long Timestamp, string Value, bool Accepted);

    public class NtInstance
    {
        public static NtInstance LastCreated;
        public readonly ConcurrentQueue<Write> Writes = new();
        public long Clock = 1_000_000;
        public bool RecordWrites = true;
        public Action<string> BeforeSet;
        public Func<string, bool> Accept = _ => true;
        public NtInstance(string root) { LastCreated = this; }
        public long Now() => Clock;
        public bool IsConnected() => false;
        public void Flush() { if (RecordWrites) Writes.Enqueue(new("<flush>", 0, "", true)); }
        public bool Set(string path, object value, long timestamp)
        {
            if (!RecordWrites) return Accept(path);
            // Freeze each call's complete input, including reusable protobuf/array values.
            string encoded = Encode(value);
            BeforeSet?.Invoke(path);
            bool accepted = Accept(path);
            Writes.Enqueue(new(path, timestamp, encoded, accepted));
            return accepted;
        }
        static string Encode(object value) => value switch
        {
            double d => BitConverter.DoubleToInt64Bits(d).ToString("X16"),
            double[] a => string.Join(",", a.Select(d => BitConverter.DoubleToInt64Bits(d).ToString("X16"))),
            IMessage m => Convert.ToBase64String(m.ToByteArray()),
            string[] a => string.Join(",", a),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? "<null>",
        };
        public DoublePublisher GetDoublePublisher(string p, PubSubOptions o) => new(this, p);
        public DoubleArrayPublisher GetDoubleArrayPublisher(string p, PubSubOptions o) => new(this, p);
        public IntegerPublisher GetIntegerPublisher(string p, PubSubOptions o) => new(this, p);
        public BooleanPublisher GetBooleanPublisher(string p, PubSubOptions o) => new(this, p);
        public StringPublisher GetStringPublisher(string p, PubSubOptions o) => new(this, p);
        public StringArrayPublisher GetStringArrayPublisher(string p, PubSubOptions o) => new(this, p);
        public StringPublisher GetStringEntry(string p, PubSubOptions o) => new(this, p);
        public ProtobufPublisher<T> GetProtobufPublisher<T>(string p, object descriptor, PubSubOptions o) => new(this, p);
        public ProtobufSubscriber<T> GetProtobufSubscriber<T>(string p, string schema, PubSubOptions o) => new();
        public BooleanSubscriber GetBooleanSubscriber(string p, PubSubOptions o) => new();
        public void SetTeamNumber(int team, int port) { }
        public void SetAddresses((string addr, int port)[] addresses) { }
        public PolledLogger CreateLogger(int min, int max) => new();
    }
    public class Publisher
    {
        readonly NtInstance nt;
        readonly string path;
        public Publisher(NtInstance nt, string path) { this.nt = nt; this.path = path; }
        public bool Set(object value, long timestamp = 0) => nt.Set(path, value, timestamp);
    }
    public class DoublePublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class DoubleArrayPublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class IntegerPublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class BooleanPublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class StringPublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class StringArrayPublisher(NtInstance n, string p) : Publisher(n, p) { }
    public class ProtobufPublisher<T>(NtInstance n, string p) : Publisher(n, p) { }
    public struct TimestampedValue<T> { public T Value; public long Timestamp; }
    public class ProtobufSubscriber<T> { public TimestampedValue<T>[] ReadQueueValues() => Array.Empty<TimestampedValue<T>>(); }
    public class BooleanSubscriber { public bool Get(bool fallback) => fallback; }
    public class PolledLogger { public (string filename, string message)[] PollForMessages() => Array.Empty<(string, string)>(); }
}
namespace QuestX.Config
{
    public interface IConfigManager
    {
        event Action<int> OnTeamNumberChanged;
        event Action<string> OnDebugIpOverrideChanged;
        event Action<bool> OnEnableDebugLoggingChanged;
    }
    public sealed class ConfigStub : IConfigManager
    {
        public event Action<int> OnTeamNumberChanged { add { } remove { } }
        public event Action<string> OnDebugIpOverrideChanged { add { } remove { } }
        public event Action<bool> OnEnableDebugLoggingChanged { add { } remove { } }
    }
}
namespace QuestX.Network
{
    public interface INtCameraSource { }
    public class NtCameraSource : INtCameraSource { public NtCameraSource(global::QuestX.Native.NTCore.NtInstance nt, string name) { } }
    // DNS is covered separately by network-address; never initiate network I/O here.
    public class LocalAddressMonitor
    {
        public const string NoAddress = "127.0.0.1";
        public string Address => null;
        public event Action<string> Changed { add { } remove { } }
        public void Poll() { }
    }
}
namespace QuestX.Utils
{
    public static class QueuedLogger
    {
        public static readonly ConcurrentQueue<string> Warnings = new();
        public static void Log(string message) { }
        public static void LogWarning(string message) => Warnings.Enqueue(message);
    }
}
namespace UnityEngine
{
    public struct Quaternion { }
    public static class Application { public static string version => "portable-test"; }
}
