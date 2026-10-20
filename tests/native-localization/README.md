# QuestX transport capture

`quest_nt_capture.py` subscribes without publishing commands, pose resets, configuration,
or robot outputs. `verify_physical_capture.py` retains its old filename but performs
**capture transport checks only**. Neither script establishes physical provenance or
accuracy. No headset is needed for offline tests or the explicitly synthetic loopback.

Run commands from the repository root. Capture needs RobotPy's
[`pyntcore==2026.2.2`](https://pypi.org/project/pyntcore/2026.2.2/) (stable 2026 release,
verified against the installed APIs). Offline verification uses only Python's stdlib.
Use a supported Python with a wheel available for your platform; Python 3.14 on macOS
ARM64 was exercised locally.

```sh
python3 -m venv work/napa/capture-venv
work/napa/capture-venv/bin/python -m pip install pyntcore==2026.2.2
work/napa/capture-venv/bin/python tests/native-localization/quest_nt_capture.py \
  --output work/napa/capture.jsonl --duration 30
python3 tests/native-localization/verify_physical_capture.py work/napa/capture.jsonl
```

On Windows, use the venv's `Scripts/python.exe` instead of `bin/python`.
The output defaults to `work/napa/capture.jsonl` relative to the current directory;
existing files are never overwritten. Duration defaults to 30 seconds and must be
above 0 and at most 3600. Ctrl+C saves a final drain and end record. Runtime, file,
and shutdown errors exit nonzero; an interrupted or duration-ended capture can be
complete even if no measurements arrived. The offline verifier then fails if v4 or
VIO is missing. Keep `work/` ignored using your local repository excludes if needed.

The default is a localhost server: `--listen 127.0.0.1`, NT4 `--port4 5810`, legacy
NT3 `--port3 1735`; persistence is disabled. Binding to a network interface requires
an explicit `--listen` argument. For USB, the operator may run the following on a
machine with a connected, authorized headset (these tools never run ADB):

```sh
adb reverse tcp:5810 tcp:5810
```

Configure the Quest app to connect to localhost:5810. Use the app's normal connection
setup; capture does not change it. Alternatively, attach to an existing NT4 server:

```sh
work/napa/capture-venv/bin/python tests/native-localization/quest_nt_capture.py \
  --server 127.0.0.1 --port4 5810 --output work/napa/client-capture.jsonl --duration 30
```

The capture logs `/QuestX/v2/vio`, `/QuestX/v4/apriltag`, `/QuestX/v3/apriltag`, and
Limelight robot/headset poses and stddevs. It records **every queued update**, including
duplicates, using `sendAll`, a 4096-entry queue per topic, and 10 ms polling. These
finite buffers can overflow; this does not promise every published frame arrived.
A metadata record declares the schema, subscriptions, and clocks; sample records store
path, array, dequeue monotonic/wall time, NT local/server value timestamps, NT local
clock at dequeue, and the contemporaneous local-to-server offset (null until synced).
Nonfinite received numbers are preserved as `NaN`/`+Infinity`/`-Infinity` strings and
fail verification. The final end record supplies counts and shutdown status.

The verifier follows [measurement-v4.md](../../protos/measurement-v4.md) and the Java
wire decoder: exact `53 + 2N` length, finite values, common header, quality/count
ranges, positive-definite 6D covariance, and positive-definite planar covariance or
exactly six zeros. The zero sentinel is counted as valid 6D data with unusable planar
projection. It requires at least one valid v4 and VIO sample, valid records and a
complete end record with matching counts. It also validates received v3 arrays;
Limelight arrays are preserved without assuming the obsolete 11-value-only layout.

Timing output covers per-topic **dequeue** spans/max gaps and adjacent publisher
capture/sequence advances, repeats, regressions, and sequence gaps. VIO output counts
epoch changes and tracking loss/resume transitions. A frozen origin with fresh stamps
does not establish live tracking. Missing transitions may simply be outside the capture.
Publisher capture words, app uptime, host monotonic/wall clocks, and NT server clock
are separate domains. The recorded server offset translates the **capture host's NT
clock**, not the publisher's embedded capture words. No cross-domain latency or
independent latest-value Limelight comparison is calculated, and no cross-stream
association is inferred. Reported sequence gaps alone do not prove network loss.

Before release, use independent surveyed ground truth for accuracy and covariance
calibration, plus a moving target/robot timing check with known clock translation
and explicit sample correlation. Exercise tracking loss, recenter and robot-requested
resets on real hardware, checking epochs and the robot's capture-time fusion behavior.
These tools send no resets and do not verify reset acknowledgments, robot E2E fusion,
independent accuracy, or NEES. Stationary scatter is insufficient for those claims.

## Tests

```sh
python3 -m py_compile tests/native-localization/quest_nt_capture.py \
  tests/native-localization/verify_physical_capture.py \
  tests/native-localization/test_capture_tools.py tests/native-localization/test_nt_loopback.py
python3 -m unittest discover -s tests/native-localization -p test_capture_tools.py -v
# Real NT4, localhost only; publishes explicitly synthetic C# fixtures and VIO samples.
QUESTX_NT_SMOKE_OUTPUT=work/napa/synthetic-loopback \
  work/napa/capture-venv/bin/python -m unittest discover \
  -s tests/native-localization -p test_nt_loopback.py -v
```

The smoke test runs bounded capture-server and capture-client sessions on temporary
localhost ports. It checks all queued fixture/duplicate updates persist, the planar
sentinel, VIO epoch/loss/resume counts, and offline CLI success. Without ntcore that
test skips. It also checks occupied-port errors; on POSIX, the client session exercises
Ctrl+C shutdown. `QUESTX_NT_SMOKE_OUTPUT` optionally saves clearly named synthetic JSONL
and reports, replacing previous smoke artifacts in that directory. The generated C#
fixtures under `questx-lib/src/test/resources/measurement-v4/` are synthetic solver/
encoder test data, never evidence of a physical headset run.
