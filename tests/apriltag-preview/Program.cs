using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuestX.QuestX.AprilTag;

double now = 1000;
int assertions = 0;
var preview = new AprilTagOverlay(() => now);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
AprilTagOverlayTag[] Tags(int id) => new[] {
    new AprilTagOverlayTag(id, new AprilTagOverlayPoint(20, 20),
        new AprilTagOverlayPoint(10, 20), new AprilTagOverlayPoint(10, 10),
        new AprilTagOverlayPoint(20, 10))
};
// Distinct sentinel bytes expose mismatched image/metadata without a JPEG dependency.
void Publish(int id, int width = 640, int height = 480) =>
    preview.Publish(width, height, now, Tags(id), new byte[] { 0xff, 0xd8, (byte)id, 0xff, 0xd9 });
async Task<(JObject Meta, byte[] Jpeg)> Decode(AprilTagOverlayPacket packet)
{
    using var output = new MemoryStream();
    await packet.WriteAsync(output, CancellationToken.None);
    var bytes = output.ToArray();
    Check(bytes.LongLength == packet.ContentLength, "HTTP length must match binary packet");
    int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
    var meta = JObject.Parse(Encoding.UTF8.GetString(bytes, 4, length));
    Check((int)meta["version"] == 1, "Protocol version");
    return (meta, bytes.Skip(4 + length).ToArray());
}
Task<AprilTagOverlayPacket> Read(long after = -1, CancellationToken token = default) =>
    preview.ReadAsync(after, token);

var disabled = await Decode(await Read());
Check(!(bool)disabled.Meta["enabled"] && disabled.Jpeg.Length == 0, "Disabled detector has no image");
preview.SetEnabled(true);
Check(!preview.IsRequested, "Detector alone must not encode previews");
var streamOff = await Decode(await Read());
Check(!(bool)streamOff.Meta["streamEnabled"] && streamOff.Jpeg.Length == 0, "Stream disabled is enforced server-side");
preview.SetStreamEnabled(true);
var pendingA = Read();
Check(!pendingA.IsCompleted && preview.IsRequested, "Viewer waits and requests an encode");
Publish(7);
var packetA = await pendingA;
var a = await Decode(packetA);
long aId = (long)a.Meta["frameId"];
Check((int)a.Meta["tags"][0]["id"] == a.Jpeg[2], "Frame A contains its own tag data");
Check(preview.IsRequested, "Viewer lease keeps encoding while the browser transfers/decodes its frame");

// Two viewers share one encode; a slow viewer skips to the latest complete frame.
var viewers = Enumerable.Range(0, 8).Select(_ => Read(aId)).ToArray();
Publish(9, 1280, 720);
foreach (var packet in await Task.WhenAll(viewers))
{
    var received = await Decode(packet);
    Check((int)received.Meta["tags"][0]["id"] == 9 && received.Jpeg[2] == 9, "Shared frame stays paired");
    Check((int)received.Meta["width"] == 1280 && (int)received.Meta["height"] == 720, "Resolution travels with image");
}
var b = await Decode(await Read(aId));
long bId = (long)b.Meta["frameId"];
Check(b.Jpeg[2] == 9 && bId != aId && preview.IsRequested, "Slow reader gets latest and keeps the bounded viewer lease");
// No waiting browser request: intermediate frames replace one another rather than queueing.
Publish(20);
Publish(21);
var skipped = await Decode(await Read(bId));
Check(skipped.Jpeg[2] == 21 && (int)skipped.Meta["tags"][0]["id"] == 21,
    "Slow browser receives only the newest complete image and matching detections");
bId = (long)skipped.Meta["frameId"];
var next = Read(bId);
Publish(11);
await next;
var stillA = await Decode(packetA);
Check(stillA.Jpeg[2] == 7 && (int)stillA.Meta["tags"][0]["id"] == 7, "In-flight responses survive newer publications");

var latest = await Decode(await Read());
var waiting = Read((long)latest.Meta["frameId"]);
preview.Clear();
var reset = await Decode(await waiting);
Check(reset.Jpeg.Length == 0 && (int)reset.Meta["width"] == 0, "Mode change wakes viewers and invalidates old image");
Check((long)reset.Meta["frameId"] != (long)latest.Meta["frameId"], "Invalidation has a distinct revision");

var emptyPending = Read();
preview.Publish(640, 480, now, Array.Empty<AprilTagOverlayTag>(), new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
var empty = await Decode(await emptyPending);
Check(empty.Jpeg.Length > 0 && !empty.Meta["tags"].Any(), "Zero detections still deliver live matched video");
var heartbeat = await Decode(await Read((long)empty.Meta["frameId"]));
Check(heartbeat.Jpeg.Length == 0 && (double)heartbeat.Meta["ageMs"] == -1, "Heartbeat never resends a duplicate image");

now += AprilTagOverlay.MaxAgeMs;
var stale = await Decode(await Read());
Check(stale.Jpeg.Length == 0, "Old frames are not served even to new viewers");
using (var cancellation = new CancellationTokenSource())
{
    var canceledRead = Read(-1, cancellation.Token);
    cancellation.Cancel();
    try { await canceledRead; throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { assertions++; }
}
now += 3001;
Check(!preview.IsRequested, "Abandoned viewer lease expires");
Publish(13);
var afterLease = Read();
Check(!afterLease.IsCompleted, "Expired lease must not publish or revive cached video");
Publish(14);
var revived = await Decode(await afterLease);
Check(revived.Jpeg[2] == 14, "New viewer gets a newly captured frame");

// Client may retain a larger sequence number after a headset restart.
var restarted = await Decode(await Read(long.MaxValue));
Check(restarted.Jpeg[2] == 14, "Sequence comparison recovers from server restart");
var stopPending = Read((long)revived.Meta["frameId"]);
preview.SetStreamEnabled(false);
var stopped = await Decode(await stopPending);
Check(!preview.IsRequested && stopped.Jpeg.Length == 0 && !(bool)stopped.Meta["streamEnabled"], "Disabling stream wakes pending reads and stops work");
Console.WriteLine($"PASS: {assertions} synchronized-preview checks.");
