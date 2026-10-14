import json,time,urllib.request
from pathlib import Path
root=Path('D:/Projects/QuestNav-rework')
def request(path,body=None):
    req=urllib.request.Request('http://127.0.0.1:5801/api/'+path,
        data=None if body is None else json.dumps(body).encode(),
        headers={'Content-Type':'application/json'})
    text=urllib.request.urlopen(req,timeout=10).read().decode('utf-8-sig')
    return json.loads(text[text.index('{'):])
before=request('status')
disabled=None
try:
    request('config',{'enableAprilTagDetector':False})
    time.sleep(1.0)
    disabled=request('status')
finally:
    request('config',{'enableAprilTagDetector':True})
time.sleep(3.0)
after=request('status')
assert disabled['aprilTag']['detectorActive'] is False
assert after['aprilTag']['detectorActive'] is True
assert after['aprilTag']['cameraFrameCount']>before['aprilTag']['cameraFrameCount']
assert after['aprilTag']['cameraFailureCount']==before['aprilTag']['cameraFailureCount']
assert after['vioPublishCount']>before['vioPublishCount']
report={'passed':True,'before':before,'disabled':disabled,'after':after}
(root/'submission/detector-restart-check.json').write_text(json.dumps(report,indent=2))
print(json.dumps({'passed':True,'disabled':disabled['aprilTag']['detectorActive'],
    'restoredEnabled':after['aprilTag']['detectorActive'], 'finalOutcome':after['aprilTag']['lastOutcome'],
    'finalTagIds':after['aprilTag']['tagIds']}))
