# Frame worker and cadence checks

Run `dotnet run --project tests/frame-worker/FrameWorkerChecks.csproj` from the repository root (.NET 8+).

Compiles the production `LatestOnlyWorker` with a collecting logger in place of Unity logging. Checks that a busy worker processes only the newest pending frame, releases replaced and abandoned frames, survives a processing exception, rejects work after stopping, and can be joined again after a timeout. Gates control the running frame so the queue and shutdown scenarios are deterministic.

It also compiles production `FrameCadence` and checks 24 FPS pacing on 30/60/120 Hz input, 30 FPS on 60 Hz input, input slower than the requested rate, mode resets, and recovery after a long stall without catch-up bursts.

These checks do not exercise GPU readback, Unity callbacks, native detectors, or on-device frame rate.
