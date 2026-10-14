import json, math
from pathlib import Path

root=Path('D:/Projects/QuestNav-rework')
s=json.loads((root/'submission/usb-networktables/physical-tag.json').read_text())
a=s['latest']['/QuestX/v2/apriltag']
v=s['latest']['/QuestX/v2/vio']
ll=s['latest']['/QuestX/limelight/botpose_wpiblue']
tag=a['value']; vio=v['value']; pose=ll['value']
assert len(vio)==14 and vio[12]==1
assert len(tag)==23+int(tag[12]) and tag[12]>=1
assert all(math.isfinite(x) for x in tag+vio+pose)
assert tag[13]>=4 and tag[13]<=tag[14] and .75<=tag[15]<=1
assert all(x>0 for x in tag[17:23])
assert all(abs(sum(x*x for x in data[8:12])-1)<1e-4 for data in [tag,vio])
assert len(pose)==11 and pose[:3]==tag[5:8] and pose[7]==tag[12]
w,x,y,z=tag[8:12]
yaw=math.degrees(math.atan2(2*(w*z+x*y),1-2*(y*y+z*z)))
assert abs(yaw-pose[5])<1e-6
transport_latency=(ll['serverTime']-a['serverTime'])/1000
assert abs(transport_latency-pose[6])<.001
assert s['diagnostics']['tv']==1 and s['diagnostics']['layoutLoaded'] is True
assert s['diagnostics']['layoutTagCount']==32
report={
    'passed':True,'source':'Physical tag ID 2 viewed by USB Quest 3S, actual production app and NT4 server',
    'tagIds':tag[23:], 'vioLength':len(vio),'tagLength':len(tag),'limelightLength':len(pose),
    'fieldHeadsetPoseMeters':tag[5:8],'yawDegrees':yaw,
    'captureToPublicationMilliseconds':pose[6],
    'ntServerTimestampDeltaMilliseconds':transport_latency,
    'note':'This target was held in a room. This verifies measurement/transport, not official-field ground-truth accuracy.'}
(root/'submission/physical-transport-verification.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
