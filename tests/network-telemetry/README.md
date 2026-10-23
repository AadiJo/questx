# Network telemetry checks

Run from the repository root with .NET 8+:

```sh
dotnet run --project tests/network-telemetry/NetworkTelemetryChecks.csproj -c Release
```

The suite compiles the actual `NetworkTableConnection`, `MeasurementTelemetry`, and
`LimelightTelemetry`, the production topic constants, and generated protobuf messages.
It references the existing measurement-checks project for the real geometry, covariance,
measurement, wire-format, and Limelight-format classes; no new package is required.
Generate the C# messages in `unity/Assets/QuestX/Generated` first, as described in
`tests/measurement-checks/README.md` (this suite also needs `Data.cs` and `Commands.cs`).

Only NTCore, Unity's application version, configuration events, camera-source creation,
logging, and the local address source are portable stand-ins. Address lookup behavior has
its own `tests/network-address` suite; these checks never perform DNS or native I/O.

Coverage:

- Five complete before/after write traces: 915 publisher calls and flushes. Each trace
  includes path, explicit timestamp, complete value, order, and return success/failure.
  Doubles are encoded as their exact IEEE bits; actual protobuf messages are serialized
  to bytes. The stored SHA-256 digests cover no sample, 10 Hz gating, freshness at and
  beyond 250 ms, heartbeat, diagnostics, v1/v2/v3/v4 and Limelight output, failed v4
  counts/log throttling/recovery, failed VIO, changing tag counts, repeated calls,
  future capture timestamps, and sequence/capture values wider than 32 bits.
- Gated worker writes at v1, v2, v3, v4, and the first/last Limelight publication.
  Main-thread VIO must reach Flush, and diagnostics must complete, before releasing
  any gate. Each tick must see the entire previous completed snapshot, then the new
  snapshot after release, including a failed v4 publication and the no-sample case.
- Gated main telemetry while the worker publishes successive distinguishable samples,
  reuses and resizes its buffers, and fails v4. The held tick must retain its old
  sequence, pose, tag arrays, count, and Limelight freshness together; the next tick
  must see the latest completed sample. This also proves main native writes do not
  hold the worker publication lock.
- Gated diagnostics while worker and VIO publication complete, and concurrent worker
  callers that must preserve complete publication batches and sequence ordering.

The diagnostics caller still owns its 3 Hz scheduling; these tests verify that every
invocation produces the same six writes. They do not execute Unity scheduling or NTCore.
All concurrency gates and joins have bounded timeouts. No telemetry tick is skipped.

`Baseline.json` was recorded by compiling the pre-refactor production files, captured
before editing under `work/optimization-implement/telemetry/before`. The fixture poses
use exact unit quaternions to avoid platform-dependent arbitrary-angle trig rounding.
To inspect complete current traces rather than digests:

```sh
dotnet run --project tests/network-telemetry/NetworkTelemetryChecks.csproj -c Release -- --trace /tmp/telemetry-traces.json
```

To reproduce the before/after comparison with an explicitly preserved old source
folder, set `-p:NetworkSource=/absolute/path/to/before`. With those old sources,
`--baseline /tmp/telemetry-baseline.json` records hashes plus a `.traces.json` sidecar,
and `--expect-worker-blocking` demonstrates the original VIO Flush dependency using
the actual connection class. Never regenerate the checked-in baseline from changed
production code just to make a failing test pass.

`--allocation` disables NT recording and measures managed allocation over 1,000 warmed
publications of an existing two-tag sample. On the local .NET runtime the snapshot adds
320 bytes per measurement: one object plus one owned array of `26 + 2 * tagCount`
doubles. It adds no per-corner/pixel allocation and no snapshot allocation per VIO tick.
This is allocation accounting and a blocking-order regression suite, not headset timing,
IL2CPP, transport delivery, or native NTCore validation.
