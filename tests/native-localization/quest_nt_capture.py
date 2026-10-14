"""Temporary USB NT server and evidence capture. No robot outputs are published."""
import json
import time
from pathlib import Path
import ntcore

out = Path('D:/Projects/QuestNav-rework/submission/usb-networktables')
out.mkdir(parents=True, exist_ok=True)
inst = ntcore.NetworkTableInstance.create()
inst.startServer('', '127.0.0.1', 1735, 5810)
paths = ['/QuestX/v2/vio', '/QuestX/v2/apriltag', '/QuestX/limelight/botpose_wpiblue',
         '/QuestX/limelight/headsetpose_wpiblue', '/QuestX/limelight/stddevs',
         '/QuestX/limelight/tagIds', '/QuestX/limelight/detectedIds']
subs = {p: inst.getDoubleArrayTopic(p).subscribe([], ntcore.PubSubOptions(sendAll=True, pollStorage=256)) for p in paths}
entries = {p: inst.getEntry('/QuestX/limelight/'+p) for p in [
    'tv','tid','hb','ageMilliseconds','latencyMilliseconds','layoutLoaded','layoutTagCount',
    'fieldLayout','status','reason','poseReference']}
print('READY 127.0.0.1:5810', flush=True)
started = time.monotonic()
counts = {p: 0 for p in paths}
last = {}
while not (out/'stop').exists():
    for p, sub in subs.items():
        queue = sub.readQueue()
        counts[p] += len(queue)
        if queue:
            v = queue[-1]
            last[p] = {'value':list(v.value), 'time':v.time, 'serverTime':v.serverTime,
                       'serverNow':ntcore._now()}
    diagnostics = {}
    for p, e in entries.items():
        v = e.getValue()
        if v.isValid(): diagnostics[p] = v.value()
    snapshot = {'elapsedSeconds': time.monotonic()-started,
                'connected': inst.isConnected(), 'counts':counts,
                'latest':last, 'diagnostics':diagnostics}
    (out/'latest.json').write_text(json.dumps(snapshot,indent=2))
    time.sleep(0.25)
inst.stopServer()
