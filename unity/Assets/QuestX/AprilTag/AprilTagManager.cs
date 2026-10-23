using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LibJpegTurboUnity;
using Meta.XR;
using QuestX.Camera;
using QuestX.Config;
using QuestX.Core;
using QuestX.Native.AprilTag;
using QuestX.Native.Napa;
using QuestX.Native.PoseLib;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using UnityEngine;
using TagDetectorBackend = QuestX.Config.Config.TagDetectorBackend;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Runs AprilTag localization off the Unity main thread. The main thread only decides which
    /// camera frames to use and starts a non-blocking GPU readback of each; a dedicated worker
    /// converts, detects, solves, and publishes the measurement as soon as it is built.
    /// Rendering therefore never waits on the GPU copy or the detector, detection runs at the
    /// camera's rate rather than being capped by (and slowing) the app's frame rate, and the
    /// robot's sample does not wait for the next Unity frame.
    /// </summary>
    public class AprilTagManager
    {
        /// <summary>
        /// Stable identifier used by AprilTagManager with the camera arbiter.
        /// </summary>
        private const string CameraArbiterRequesterId = "AprilTagManager";

        /// <summary>
        /// Frames in flight at once: one the worker is processing, one waiting for it, and one
        /// being read back. The third lets a newer frame replace the waiting one instead of
        /// the worker always taking a frame that aged while it was busy.
        /// </summary>
        private const int FrameSlotCount = 3;

        /// <summary>How long disable waits for the worker to finish its current frame.</summary>
        private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(2);

        private readonly AprilTagFieldLayout aprilTagFieldLayout;
        private readonly Func<long> captureTimestampProvider;
        private readonly PassthroughCameraAccess cameraAccess;
        private readonly CameraResourceManager cameraArbiter;
        private readonly IConfigManager configManager;

        // Owned by the worker thread while it runs: created on enable before the worker starts,
        // swapped by the worker on a backend change, and disposed after the worker stops.
        private ITagDetector tagDetector;
        private TagDetectorBackend detectorBackend = TagDetectorBackend.NAPA;

        /// <summary>Backend the worker should run; it swaps detectors before its next frame.</summary>
        private volatile int requestedBackend = (int)TagDetectorBackend.NAPA;
        private TagDetectorBackend runningBackend;

        /// <summary>Worker-owned; filled by <see cref="ITagDetector.Detect"/>.</summary>
        private readonly List<TagDetection> detections = new List<TagDetection>();

        /// <summary>Worker-owned. Intrinsics come from each frame (see <see cref="FrameSlot"/>).</summary>
        private readonly PoseLibSolver poseLibSolver;
        private int appliedIntrinsicsVersion = -1;

        private readonly MonoBehaviour coroutineHost;
        private Coroutine captureCoroutine;
        private float frameDelaySeconds;

        private readonly FrameSlot[] frameSlots = new FrameSlot[FrameSlotCount];
        private readonly object slotLock = new object();
        private LatestOnlyWorker<FrameSlot> worker;

        /// <summary>
        /// Changes on every enable and disable, so callbacks and results from an old run are
        /// ignored. Written on the main thread; the worker reads it to drop stale frames.
        /// </summary>
        private int pipelineGeneration;

        /// <summary>Exposure time, in seconds, of the next frame to process.</summary>
        private double nextFrameExposureSeconds;

        /// <summary>
        /// How early a frame may be exposed and still count as due: a quarter of the 60 Hz
        /// camera period, which absorbs timestamp jitter without taking the frame before.
        /// </summary>
        private const double FrameScheduleToleranceSeconds = 0.004;

        /// <summary>Worker-owned preview encoder and rotation scratch buffer.</summary>
        private LJTCompressor overlayCompressor;
        private byte[] overlayRotationBuffer;

        private int overlayQuality = 75;
        private double overlayFrameIntervalMs = 1000.0 / 24;
        private double nextOverlayFrameAt;

        // Informational logging is sampled; diagnostics still record every measurement.
        // nextMeasurementLogAtMs belongs to the worker.
        private double nextMeasurementLogAtMs;
        private volatile bool debugLogging;

        /// <summary>
        /// How the stream is rotated. The preview and tag angles follow it; poses never do.
        /// </summary>
        private CameraOrientation orientation;

        /// <summary>
        /// Single source of truth for the detector lifecycle. The capture coroutine checks
        /// this on every iteration; setting it to false causes the next iteration to break
        /// cleanly without touching the (about-to-be-disposed) detector or family.
        /// </summary>
        private bool detectorActive;

        /// <summary>
        /// Set of tag IDs to drop from each frame's detections (blacklist). Empty = detect every tag.
        /// Rebuilt from <c>Config.AprilTagDetectorMode.IgnoredIds</c> on every config change and
        /// never mutated afterwards, so frames on the worker can share it.
        /// </summary>
        private HashSet<int> ignoredIdSet = new HashSet<int>();
        private double maxDistance;
        private int minimumNumberOfTags;

        /// <summary>
        /// Most recently requested camera resolution. Used to skip arbiter re-reservations
        /// when only the framerate changed (the camera does not need to be bounced).
        /// </summary>
        private Vector2Int currentResolution;

        /// <summary>
        /// The Meta SDK can report the old intrinsics briefly after a camera mode change.
        /// Refresh on the first delivered frame at the new resolution, then skip that frame
        /// so no solve can mix new pixels with stale calibration.
        /// </summary>
        private bool intrinsicsStale = true;
        private Vector2Int cachedIntrinsicsResolution;
        private PassthroughCameraAccess.CameraIntrinsics cachedIntrinsics;
        private int cachedIntrinsicsVersion;

        private readonly object diagnosticsLock = new object();
        private long cameraFrameCount;
        private long cameraFailureCount;
        private double lastCameraRecoveryAppTimestamp = double.NegativeInfinity;
        private long solveCount;
        private long measurementCount;
        private double lastMeasurementAppTimestamp;
        private int[] lastTagIds = Array.Empty<int>();
        private int[] detectedIds = Array.Empty<int>();
        private int lastInlierCount;
        private int lastCornerCount;
        private double lastInlierRatio;
        private double lastAverageTagDistanceMeters;
        private double lastLinearStdDevMeters;
        private double lastAmbiguity;
        private double lastReprojectionErrorPixels;
        private string detectorName = "";
        private RunningAverage detectTime;
        private RunningAverage readbackTime;
        private RunningAverage solveTime;
        private RunningAverage previewTime;
        private RunningAverage latency;
        private double overlayRetryAtMs;
        private string lastOutcome = "Not started";
        private string lastReason = "";

        // Frame-rate counters. Camera updates and busy skips are counted on the main thread,
        // processed frames on the worker; rates are refreshed about once per second.
        private long cameraUpdateCount;
        private long busyFrameCount;
        private long processedFrameCount;
        private long rateWindowStart = Stopwatch.GetTimestamp();
        private long rateWindowCameraUpdates;
        private long rateWindowProcessed;
        private long rateWindowMeasurements;
        private double cameraFramesPerSecond;
        private double processedFramesPerSecond;
        private double measurementsPerSecond;

        /// <summary>
        /// Raised on the detection worker, as soon as PoseLib produces a field-relative AprilTag
        /// measurement that passes the Quest's detector-level validity checks. Subscribers that
        /// touch Unity state must hand off to the main thread. No sensor fusion has been applied
        /// to this measurement.
        /// </summary>
        public event Action<AprilTagMeasurement> MeasurementAvailable;

        public AprilTagOverlay Overlay { get; } = new AprilTagOverlay();

        /// <summary>Latest detections with single-tag poses, for the web UI's 3D views.</summary>
        public AprilTagScene Scene { get; } = new AprilTagScene();

        public AprilTagManager(
            IConfigManager configManager,
            PassthroughCameraAccess cameraAccess,
            CameraResourceManager cameraArbiter,
            AprilTagFieldLayout aprilTagFieldLayout,
            MonoBehaviour coroutineHost,
            Func<long> captureTimestampProvider
        )
        {
            this.cameraAccess = cameraAccess;
            this.cameraArbiter = cameraArbiter;
            this.captureTimestampProvider =
                captureTimestampProvider
                ?? throw new ArgumentNullException(nameof(captureTimestampProvider));
            this.configManager = configManager;
            this.aprilTagFieldLayout = aprilTagFieldLayout;
            this.coroutineHost = coroutineHost;
            poseLibSolver = new PoseLibSolver(aprilTagFieldLayout, cameraAccess.Intrinsics);
            for (int i = 0; i < frameSlots.Length; i++)
            {
                frameSlots[i] = new FrameSlot();
            }

            configManager.OnEnableAprilTagDetectorChanged += OnEnableAprilTagDetectorChanged;
            configManager.OnAprilTagDetectorModeChanged += OnAprilTagDetectorModeChanged;
            configManager.OnAprilTagDetectorBackendChanged += OnAprilTagDetectorBackendChanged;
            configManager.OnPassthroughStreamModeChanged += OnOverlayStreamModeChanged;
            configManager.OnEnablePassthroughStreamChanged += OnOverlayStreamEnabledChanged;
            configManager.OnCameraOrientationChanged += OnCameraOrientationChanged;
            configManager.OnEnableDebugLoggingChanged += OnEnableDebugLoggingChanged;

            // Refresh PoseLib's cached intrinsics whenever the camera arbiter changes
            // the effective resolution. Meta SDK exposes per-resolution intrinsics
            // (focal length, principal point, sensor resolution all change with the
            // requested resolution), and PoseLib silently produces a wrong pose if
            // we solve with stale values.
            cameraArbiter.OnResolutionChanged += OnCameraArbiterResolutionChanged;

            QueuedLogger.Log("Initialized AprilTagManager");
        }

        /// <summary>
        /// One camera frame on its way through the pipeline: a readback buffer plus everything
        /// the worker needs, captured on the main thread when the frame was chosen so the worker
        /// never reads Unity, SDK or config state.
        /// </summary>
        private sealed class FrameSlot
        {
            public readonly CameraFrameReadback Readback = new CameraFrameReadback();
            public SlotState State;
            public int Generation;

            public long RequestedAt;

            /// <summary>Exposure time as UTC <see cref="DateTime.Ticks"/>, for latency.</summary>
            public long ExposureUtcTicks;
            public long CaptureTimestampMicros;
            public double CaptureAppTimestamp;

            /// <summary>Exposure time on <see cref="AprilTagOverlay.MonotonicMilliseconds"/>.</summary>
            public double CapturedAtMs;
            public int Width;
            public int Height;
            public PassthroughCameraAccess.CameraIntrinsics Intrinsics;
            public int IntrinsicsVersion;
            public Pose LensOffset;
            public HashSet<int> IgnoredIds;
            public double MaxDistance;
            public int MinimumNumberOfTags;
            public CameraOrientation Orientation;
            public bool BuildOverlay;
            public int OverlayQuality;
            public bool BuildScene;
        }

        private enum SlotState
        {
            Free,
            ReadingBack,
            Processing,
        }

        /// <summary>Exponential moving average (alpha 0.05) plus the latest sample.</summary>
        private struct RunningAverage
        {
            public double Last;
            public double Average;

            public void Add(double value)
            {
                Last = value;
                Average = Average <= 0 ? value : Average + 0.05 * (value - Average);
            }
        }

        private static double ElapsedMilliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Called when the camera arbiter applies a new effective resolution. The Meta SDK's
        /// intrinsics can still describe the previous camera mode during this callback, so
        /// defer the refresh until a frame at the new resolution is actually available.
        /// </summary>
        private void OnCameraArbiterResolutionChanged(Vector2Int? newResolution)
        {
            Overlay.Clear();
            Scene.Clear();
            if (!newResolution.HasValue)
            {
                return;
            }

            intrinsicsStale = true;
        }

        private bool TryRefreshCameraIntrinsics(int targetWidth, int targetHeight)
        {
            try
            {
                if (cameraAccess == null)
                {
                    return false;
                }

                // The worker applies these to PoseLib before solving the next frame that
                // carries this version.
                cachedIntrinsics = cameraAccess.Intrinsics;
                cachedIntrinsicsVersion++;
                cachedIntrinsicsResolution = new Vector2Int(targetWidth, targetHeight);
                intrinsicsStale = false;
                QueuedLogger.Log($"PoseLib intrinsics refreshed for {targetWidth}x{targetHeight}");
                return true;
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"Failed to refresh PoseLib intrinsics after resolution change: {ex.Message}"
                );
                return false;
            }
        }

        private void OnEnableAprilTagDetectorChanged(bool enable)
        {
            if (enable)
            {
                if (detectorActive)
                {
                    QueuedLogger.LogWarning(
                        "AprilTag detector enable requested but already active; ignoring."
                    );
                    return;
                }

                // Refuse to reserve the camera at an invalid resolution. ConfigManager
                // is supposed to fire OnAprilTagDetectorModeChanged before
                // OnEnableAprilTagDetectorChanged at startup so currentResolution is
                // already valid; this guard catches the case where someone toggles
                // Enable directly via the API without a prior Mode write. A camera
                // reservation at (0, 0) puts the underlying Android camera into a bad
                // state and crashes libapriltag on the first frame.
                if (currentResolution.x <= 0 || currentResolution.y <= 0)
                {
                    QueuedLogger.LogError(
                        "AprilTag detector enable requested but no valid resolution has been "
                            + $"configured (currentResolution={currentResolution.x}x{currentResolution.y}). "
                            + "Skipping enable; set the detection resolution first."
                    );
                    return;
                }

                // A previous worker that did not stop in time may still hold the detector and a
                // frame buffer; starting another would share them. Free them if it has exited.
                if (!StopWorker(TimeSpan.Zero))
                {
                    QueuedLogger.LogError(
                        "AprilTag detector enable requested while the previous detection worker is still running; ignoring."
                    );
                    return;
                }

                try
                {
                    SetDetector(CreateDetector(detectorBackend), detectorBackend);
                }
                catch (Exception ex)
                {
                    QueuedLogger.LogError($"Failed to allocate tag detector: {ex.Message}");
                    SetDetector(null, detectorBackend);
                    return;
                }

                // Reserve the camera at high priority. The arbiter will downgrade the
                // passthrough stream's reservation if it conflicts. Reserve is async
                // (posts to main thread); the coroutine waits for cameraAccess.enabled
                // before reading frames so the early frames before the arbiter has
                // applied the reservation are skipped instead of crashing.
                cameraArbiter.Reserve(
                    CameraArbiterRequesterId,
                    currentResolution,
                    CameraRequestPriority.High
                );

                intrinsicsStale = true;
                nextFrameExposureSeconds = 0;
                nextMeasurementLogAtMs = 0;
                Interlocked.Increment(ref pipelineGeneration);
                requestedBackend = (int)detectorBackend;
                worker = new LatestOnlyWorker<FrameSlot>(
                    "AprilTag detection",
                    ProcessFrameOnWorker,
                    ReleaseSlot
                );
                detectorActive = true;
                Overlay.SetEnabled(true);
                captureCoroutine = coroutineHost.StartCoroutine(AprilTagFrameCaptureCoroutine());
                RecordOutcome("Waiting for camera frame", "");
                QueuedLogger.Log("AprilTagDetector Enabled");
            }
            else
            {
                if (!detectorActive)
                {
                    QueuedLogger.LogWarning(
                        "AprilTag detector disable requested but already inactive; ignoring."
                    );
                    return;
                }

                // Flip the active flag FIRST so the coroutine and readback callbacks stop
                // feeding the worker.
                detectorActive = false;
                Interlocked.Increment(ref pipelineGeneration);
                Overlay.SetEnabled(false);
                Scene.Clear();

                if (captureCoroutine != null)
                {
                    coroutineHost.StopCoroutine(captureCoroutine);
                    captureCoroutine = null;
                }

                try
                {
                    cameraArbiter.Release(CameraArbiterRequesterId);
                }
                catch (Exception ex)
                {
                    QueuedLogger.LogError(
                        $"AprilTag detector disable: cameraArbiter.Release failed: {ex.Message}"
                    );
                }

                StopWorker(WorkerStopTimeout);
                RecordOutcome("Detector disabled", "");
                QueuedLogger.Log("AprilTagDetector Disabled");
            }
        }

        /// <summary>
        /// Stops the detector and frees everything it holds. Call when the app shuts down.
        /// </summary>
        public void Shutdown()
        {
            if (detectorActive)
            {
                OnEnableAprilTagDetectorChanged(false);
            }
            else
            {
                StopWorker(WorkerStopTimeout);
            }
        }

        /// <summary>
        /// Stops the worker after its current frame, then frees the detector, the preview encoder
        /// and the frame buffers. If the worker is stuck in native code, its resources are left
        /// allocated rather than freed underneath it, and a later call retries. Returns whether
        /// no worker remains.
        /// </summary>
        private bool StopWorker(TimeSpan timeout)
        {
            if (worker == null)
            {
                return true;
            }
            if (!worker.Stop(timeout))
            {
                if (timeout > TimeSpan.Zero)
                {
                    QueuedLogger.LogError(
                        $"AprilTag detection worker did not stop within {timeout.TotalSeconds:F0} s; "
                            + "leaving its detector and frame buffers allocated."
                    );
                }
                return false;
            }
            worker = null;

            // The worker can no longer publish, so a scene or preview frame it published after
            // disable cleared them goes now.
            if (!detectorActive)
            {
                Scene.Clear();
                Overlay.Clear();
            }

            try
            {
                tagDetector?.Dispose();
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"AprilTag detector disable: detector dispose failed: {ex.Message}"
                );
            }
            SetDetector(null, detectorBackend);
            overlayCompressor?.Dispose();
            overlayCompressor = null;
            overlayRotationBuffer = null;

            lock (slotLock)
            {
                foreach (var slot in frameSlots)
                {
                    // Waits for any copy still in flight, since the GPU writes into the buffer.
                    slot.Readback.Dispose();
                    slot.State = SlotState.Free;
                }
            }
            return true;
        }

        /// <summary>
        /// Allocates the requested backend. If Napa's native library cannot be loaded (for
        /// example a build without libnapa.so), fall back to libapriltag so localization keeps
        /// working; diagnostics report which backend actually runs.
        /// </summary>
        private static ITagDetector CreateDetector(TagDetectorBackend backend)
        {
            if (backend == TagDetectorBackend.NAPA)
            {
                try
                {
                    return new NapaTagDetector(
                        QuestXConstants.AprilTag.NAPA_MIN_TAG_PIXELS,
                        QuestXConstants.AprilTag.NAPA_MAX_HAMMING
                    );
                }
                catch (Exception ex)
                    when (ex is DllNotFoundException
                        || ex is EntryPointNotFoundException
                        || ex is InvalidOperationException
                    )
                {
                    QueuedLogger.LogError(
                        $"Napa tag detector unavailable ({ex.Message}); falling back to libapriltag."
                    );
                }
            }
            return new LibAprilTagDetector();
        }

        private void SetDetector(ITagDetector detector, TagDetectorBackend backend)
        {
            tagDetector = detector;
            runningBackend = backend;
            lock (diagnosticsLock)
            {
                detectorName = detector?.Name ?? "";
                detectTime = default;
            }
        }

        private void OnAprilTagDetectorBackendChanged(TagDetectorBackend backend)
        {
            detectorBackend = backend;
            // The worker owns the detector while it runs; it swaps before its next frame.
            requestedBackend = (int)backend;
        }

        /// <summary>
        /// Worker thread. Switches to the requested backend; keeps the current one if the new
        /// one cannot be allocated.
        /// </summary>
        private void ApplyRequestedBackend()
        {
            var backend = (TagDetectorBackend)requestedBackend;
            if (tagDetector != null && backend == runningBackend)
            {
                return;
            }

            ITagDetector replacement;
            try
            {
                replacement = CreateDetector(backend);
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"Failed to allocate {backend} tag detector; keeping {tagDetector?.Name ?? "none"}. {ex.Message}"
                );
                runningBackend = backend;
                return;
            }

            var previous = tagDetector;
            SetDetector(replacement, backend);
            Overlay.Clear();
            try
            {
                previous?.Dispose();
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError($"Tag detector dispose failed: {ex.Message}");
            }
            QueuedLogger.Log($"Tag detector switched to {replacement.Name}");
        }

        private void OnAprilTagDetectorModeChanged(Config.Config.AprilTagDetectorMode configuration)
        {
            Overlay.Clear();
            Scene.Clear();
            if (
                configuration.Mode
                == Config.Config.AprilTagDetectorMode.DetectionMode.ANCHOR_ENHANCED
            )
            {
                throw new NotImplementedException(
                    "ANCHOR_ENHANCED has not been implemented yet! Please check for a later release."
                );
            }

            // Cache the requested resolution so OnEnableAprilTagDetectorChanged(true)
            // can re-reserve at the right size, and so we can detect FPS-only changes
            // below to skip an unnecessary camera bounce.
            var requested = new Vector2Int(configuration.Width, configuration.Height);
            bool resolutionChanged = requested != currentResolution;
            currentResolution = requested;

            // Only ask the arbiter to re-apply when we are currently the active high-priority
            // requester AND the resolution actually changed. A pure FPS change does not need
            // to bounce the camera (the framerate is enforced by IsFrameDue).
            if (resolutionChanged && detectorActive)
            {
                cameraArbiter.Reserve(
                    CameraArbiterRequesterId,
                    requested,
                    CameraRequestPriority.High
                );
            }

            frameDelaySeconds = 1.0f / configuration.Framerate;
            // Replace, never mutate: frames already handed to the worker keep the set they
            // were captured with.
            ignoredIdSet =
                configuration.IgnoredIds == null
                    ? new HashSet<int>()
                    : new HashSet<int>(configuration.IgnoredIds);
            maxDistance = configuration.MaxDistance;
            minimumNumberOfTags = configuration.MinimumNumberOfTags;
        }

        /// <summary>Returns an immutable copy of the latest detector diagnostics.</summary>
        public AprilTagDiagnosticsSnapshot GetDiagnosticsSnapshot()
        {
            lock (diagnosticsLock)
            {
                return new AprilTagDiagnosticsSnapshot(
                    detectorActive,
                    cameraFrameCount,
                    cameraFailureCount,
                    solveCount,
                    measurementCount,
                    lastMeasurementAppTimestamp,
                    (int[])lastTagIds.Clone(),
                    lastInlierCount,
                    lastCornerCount,
                    lastInlierRatio,
                    lastAverageTagDistanceMeters,
                    lastLinearStdDevMeters,
                    lastOutcome,
                    lastReason
                )
                {
                    FieldLayoutFile = aprilTagFieldLayout.LoadedFile,
                    LayoutLoaded = aprilTagFieldLayout.IsLoaded,
                    LayoutTagCount = aprilTagFieldLayout.Tags?.Count ?? 0,
                    DetectedIds = (int[])detectedIds.Clone(),
                    LastAmbiguity = lastAmbiguity,
                    LastReprojectionErrorPixels = lastReprojectionErrorPixels,
                    DetectorName = detectorName,
                    LastDetectMilliseconds = detectTime.Last,
                    AverageDetectMilliseconds = detectTime.Average,
                    AverageReadbackMilliseconds = readbackTime.Average,
                    AverageSolveMilliseconds = solveTime.Average,
                    AveragePreviewMilliseconds = previewTime.Average,
                    AverageLatencyMilliseconds = latency.Average,
                    CameraFramesPerSecond = cameraFramesPerSecond,
                    ProcessedFramesPerSecond = processedFramesPerSecond,
                    MeasurementsPerSecond = measurementsPerSecond,
                    BusyFrameCount = busyFrameCount,
                };
            }
        }

        /// <summary>
        /// Paces detection against exposure times instead of sleeping after each frame. The
        /// camera delivers frames on a fixed grid, so 30 Hz takes every second 60 Hz frame;
        /// sleeping a full period after the work instead landed on every second or third
        /// frame (about 23 Hz). The schedule advances by whole periods so rates that don't
        /// divide the camera's (24 Hz) still average out, and re-anchors on this frame after a
        /// stall or a timestamp jump.
        /// </summary>
        private bool IsFrameDue(double exposureSeconds)
        {
            double early = nextFrameExposureSeconds - exposureSeconds;
            return !(early > FrameScheduleToleranceSeconds && early <= frameDelaySeconds);
        }

        /// <summary>Advances the schedule past a frame that <see cref="IsFrameDue"/> accepted.</summary>
        private void TakeFrame(double exposureSeconds)
        {
            double period = frameDelaySeconds;
            double early = nextFrameExposureSeconds - exposureSeconds;
            nextFrameExposureSeconds =
                (Math.Abs(early) > period ? exposureSeconds : nextFrameExposureSeconds) + period;
        }

        private void RecordOutcome(string outcome, string reason)
        {
            lock (diagnosticsLock)
            {
                lastOutcome = outcome;
                lastReason = reason ?? "";
            }
        }

        private void RecordCameraFailure(string reason, double appTimestamp)
        {
            lock (diagnosticsLock)
            {
                cameraFailureCount++;
            }
            RecordOutcome("Camera capture failed", reason);
            Overlay.Clear();

            // Camera access can transiently fail after a resolution change, cable
            // disturbance, or Meta service hiccup. Keep the detector alive and
            // periodically reassert the arbiter's winning reservation.
            if (appTimestamp - lastCameraRecoveryAppTimestamp >= 5.0)
            {
                lastCameraRecoveryAppTimestamp = appTimestamp;
                QueuedLogger.LogError(
                    $"AprilTag camera capture failed; retrying and reapplying camera configuration. {reason}"
                );
                cameraArbiter.ReapplyCurrentResolution();
            }
        }

        private FrameSlot TakeFreeSlot()
        {
            lock (slotLock)
            {
                foreach (var slot in frameSlots)
                {
                    if (slot.State == SlotState.Free && !slot.Readback.InFlight)
                    {
                        slot.State = SlotState.ReadingBack;
                        return slot;
                    }
                }
            }
            return null;
        }

        private bool HasFreeSlot()
        {
            lock (slotLock)
            {
                foreach (var slot in frameSlots)
                {
                    if (slot.State == SlotState.Free && !slot.Readback.InFlight)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Returns a frame's buffer to the pool. Any thread.</summary>
        private void ReleaseSlot(FrameSlot slot)
        {
            lock (slotLock)
            {
                slot.State = SlotState.Free;
            }
        }

        private IEnumerator AprilTagFrameCaptureCoroutine()
        {
            while (true)
            {
                UpdateRates();

                // Bail out cleanly if the detector was disabled between iterations.
                if (!detectorActive || worker == null)
                {
                    yield break;
                }

                // The camera arbiter applies cameraAccess.enabled = true on a Post()-queued
                // main-thread callback, but StartCoroutine runs the first MoveNext()
                // synchronously. If the very first iteration runs before the arbiter has
                // dispatched, the camera is still off; skip the iteration.
                if (!cameraAccess.enabled)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                // Only a new camera image is worth reading back; one readback per image also
                // keeps GPU copies from piling onto the render thread.
                if (!cameraAccess.IsUpdatedThisFrame)
                {
                    yield return null;
                    continue;
                }
                cameraUpdateCount++;

                // The camera timestamp is the hardware exposure time in UTC. Convert its
                // age into both app-time and NT-time domains so the robot receives the
                // measurement time rather than the later processing time. Clamp the
                // age because the wall clock can step and a bad timestamp should not poison
                // the robot estimator's history buffer.
                double exposureAgeSeconds = (DateTime.UtcNow - cameraAccess.Timestamp).TotalSeconds;
                if (exposureAgeSeconds < -0.01 || exposureAgeSeconds > 0.5)
                {
                    RecordOutcome(
                        "Camera timestamp invalid",
                        $"Exposure age {exposureAgeSeconds:F3}s"
                    );
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }
                exposureAgeSeconds = Math.Max(0.0, exposureAgeSeconds);
                double exposureSeconds =
                    cameraAccess.Timestamp.Ticks / (double)TimeSpan.TicksPerSecond;
                if (!IsFrameDue(exposureSeconds))
                {
                    yield return null;
                    continue;
                }

                // Both buffers are still being read back or detected. Drop this frame without
                // advancing the schedule, so the next camera frame is taken once one frees.
                if (!HasFreeSlot())
                {
                    busyFrameCount++;
                    yield return null;
                    continue;
                }

                double captureAppTimestamp = Math.Max(0.0, Time.timeAsDouble - exposureAgeSeconds);
                long captureTimestampMicros =
                    captureTimestampProvider() - (long)Math.Round(exposureAgeSeconds * 1_000_000.0);
                double capturedAtMs =
                    AprilTagOverlay.MonotonicMilliseconds() - exposureAgeSeconds * 1000.0;

                // The texture is CurrentResolution.x by CurrentResolution.y pixels; the
                // readback is checked against it so a frame from before a mode change is
                // never solved with the new intrinsics.
                Vector2Int currentRes = cameraAccess.CurrentResolution;
                int actualW = currentRes.x;
                int actualH = currentRes.y;
                if (actualW <= 0 || actualH <= 0)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                if (
                    intrinsicsStale
                    || cachedIntrinsicsResolution.x != actualW
                    || cachedIntrinsicsResolution.y != actualH
                )
                {
                    if (!TryRefreshCameraIntrinsics(actualW, actualH))
                    {
                        RecordOutcome(
                            "Waiting for camera intrinsics",
                            $"Intrinsics unavailable for {actualW}x{actualH}"
                        );
                        yield return new WaitForSeconds(frameDelaySeconds);
                        continue;
                    }

                    // This frame may have been captured before the refreshed intrinsics
                    // became visible. Start solving with the next delivered frame.
                    nextFrameExposureSeconds = 0;
                    yield return null;
                    continue;
                }

                Texture texture = null;
                string captureFailureReason = null;
                try
                {
                    texture = cameraAccess.GetTexture();
                }
                catch (Exception ex)
                {
                    captureFailureReason = ex.Message;
                }
                if (texture == null || texture.width != actualW || texture.height != actualH)
                {
                    RecordCameraFailure(
                        captureFailureReason
                            ?? (
                                texture == null
                                    ? "The camera returned no texture"
                                    : $"Camera texture is {texture.width}x{texture.height}, expected {actualW}x{actualH}"
                            ),
                        captureAppTimestamp
                    );
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                var slot = TakeFreeSlot();
                if (slot == null)
                {
                    busyFrameCount++;
                    yield return null;
                    continue;
                }
                TakeFrame(exposureSeconds);
                slot.Generation = Volatile.Read(ref pipelineGeneration);
                slot.RequestedAt = Stopwatch.GetTimestamp();
                slot.ExposureUtcTicks = cameraAccess.Timestamp.Ticks;
                slot.CaptureTimestampMicros = captureTimestampMicros;
                slot.CaptureAppTimestamp = captureAppTimestamp;
                slot.CapturedAtMs = capturedAtMs;
                slot.Width = actualW;
                slot.Height = actualH;
                slot.Intrinsics = cachedIntrinsics;
                slot.IntrinsicsVersion = cachedIntrinsicsVersion;
                slot.LensOffset = cameraAccess.Intrinsics.LensOffset;
                slot.IgnoredIds = ignoredIdSet;
                slot.MaxDistance = maxDistance;
                slot.MinimumNumberOfTags = minimumNumberOfTags;
                slot.Orientation = orientation;
                slot.BuildScene = Scene.IsRequested;
                slot.OverlayQuality = overlayQuality;
                double now = AprilTagOverlay.MonotonicMilliseconds();
                double overlayRetryAt;
                lock (diagnosticsLock)
                {
                    overlayRetryAt = overlayRetryAtMs;
                }
                slot.BuildOverlay =
                    Overlay.IsRequested
                    && now >= overlayRetryAt
                    && FrameCadence.TryTake(now, overlayFrameIntervalMs, ref nextOverlayFrameAt);

                if (
                    !slot.Readback.TryRequest(
                        texture,
                        succeeded => OnReadbackCompleted(slot, succeeded),
                        out string requestFailure
                    )
                )
                {
                    ReleaseSlot(slot);
                    RecordCameraFailure(requestFailure, captureAppTimestamp);
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                yield return null;
            }
        }

        /// <summary>Main thread, when a frame's GPU copy finishes.</summary>
        private void OnReadbackCompleted(FrameSlot slot, bool succeeded)
        {
            if (
                !detectorActive
                || slot.Generation != Volatile.Read(ref pipelineGeneration)
                || worker == null
            )
            {
                ReleaseSlot(slot);
                return;
            }
            if (!succeeded)
            {
                ReleaseSlot(slot);
                RecordCameraFailure(
                    "The GPU readback of the camera frame failed",
                    Time.timeAsDouble
                );
                return;
            }

            lock (diagnosticsLock)
            {
                cameraFrameCount++;
                readbackTime.Add(ElapsedMilliseconds(slot.RequestedAt));
            }
            lock (slotLock)
            {
                slot.State = SlotState.Processing;
            }
            // A frame still waiting for the worker is replaced: the newest one is worth more.
            worker.Post(slot);
        }

        /// <summary>
        /// Worker thread. Raises <see cref="MeasurementAvailable"/> right away, so the robot's
        /// sample does not wait for the next Unity frame.
        /// </summary>
        private void PublishMeasurement(FrameSlot slot, AprilTagMeasurement measurement)
        {
            // A worker that outlived its run (see StopWorker) must not publish into a new one.
            if (IsStale(slot))
            {
                return;
            }
            try
            {
                MeasurementAvailable?.Invoke(measurement);
                double ageMilliseconds =
                    (DateTime.UtcNow.Ticks - slot.ExposureUtcTicks)
                    / (double)TimeSpan.TicksPerMillisecond;
                lock (diagnosticsLock)
                {
                    measurementCount++;
                    latency.Add(ageMilliseconds);
                    lastMeasurementAppTimestamp = measurement.AppTimestamp;
                    lastTagIds = measurement.TagIds;
                    lastInlierCount = measurement.InlierCount;
                    lastCornerCount = measurement.CornerCount;
                    lastInlierRatio = measurement.InlierRatio;
                    lastAverageTagDistanceMeters = measurement.AverageTagDistanceMeters;
                    lastLinearStdDevMeters = Math.Max(
                        measurement.XStdDevMeters,
                        measurement.YStdDevMeters
                    );
                    lastAmbiguity = measurement.Ambiguity;
                    lastReprojectionErrorPixels = measurement.ReprojectionErrorPixels;
                    lastOutcome = "Measurement published";
                    lastReason = "";
                }

                double nowMs = AprilTagOverlay.MonotonicMilliseconds();
                if (debugLogging || nowMs >= nextMeasurementLogAtMs)
                {
                    nextMeasurementLogAtMs = nowMs + 1000.0;
                    var position = measurement.Pose.Translation;
                    QueuedLogger.Log(
                        $"AprilTag measurement published: headset ({position.X:F3}, {position.Y:F3}, {position.Z:F3}) "
                            + $"tags={measurement.TagIds.Length} inliers={measurement.InlierCount}/{measurement.CornerCount} "
                            + $"ratio={measurement.InlierRatio:F2} dist={measurement.AverageTagDistanceMeters:F2}m "
                            + $"stdDev=({measurement.XStdDevMeters:F4}, {measurement.YStdDevMeters:F4})m "
                            + $"yaw={measurement.YawStdDevRadians:F4}rad "
                            + $"ambiguity={measurement.Ambiguity:F3} reprojection={measurement.ReprojectionErrorPixels:F2}px "
                            + $"latency={ageMilliseconds:F0}ms"
                    );
                }
            }
            catch (Exception ex)
            {
                RecordOutcome("Measurement publish failed", ex.Message);
                QueuedLogger.LogError($"AprilTag measurement subscriber threw: {ex.Message}");
            }
        }

        /// <summary>Main thread. Refreshes the per-second rates about once per second.</summary>
        private void UpdateRates()
        {
            double seconds = ElapsedMilliseconds(rateWindowStart) / 1000.0;
            if (seconds < 1.0)
            {
                return;
            }
            long processed = Interlocked.Read(ref processedFrameCount);
            lock (diagnosticsLock)
            {
                cameraFramesPerSecond = (cameraUpdateCount - rateWindowCameraUpdates) / seconds;
                processedFramesPerSecond = (processed - rateWindowProcessed) / seconds;
                measurementsPerSecond = (measurementCount - rateWindowMeasurements) / seconds;
                rateWindowMeasurements = measurementCount;
            }
            rateWindowCameraUpdates = cameraUpdateCount;
            rateWindowProcessed = processed;
            rateWindowStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Worker thread: one frame from readback to measurement.</summary>
        private void ProcessFrameOnWorker(FrameSlot slot)
        {
            try
            {
                ProcessFrame(slot);
            }
            catch (Exception ex)
            {
                Overlay.Clear();
                RecordOutcome("Detection failed", ex.Message);
                QueuedLogger.LogError($"AprilTag frame processing failed: {ex}");
            }
            finally
            {
                Interlocked.Increment(ref processedFrameCount);
                ReleaseSlot(slot);
            }
        }

        /// <summary>Worker thread. Whether the detector was disabled or restarted since this frame.</summary>
        private bool IsStale(FrameSlot slot) =>
            slot.Generation != Volatile.Read(ref pipelineGeneration);

        private void ProcessFrame(FrameSlot slot)
        {
            if (IsStale(slot))
            {
                return;
            }
            ApplyRequestedBackend();
            if (tagDetector == null)
            {
                RecordOutcome("Detection failed", "No tag detector is allocated");
                return;
            }
            if (slot.IntrinsicsVersion != appliedIntrinsicsVersion)
            {
                poseLibSolver.RefreshIntrinsics(slot.Intrinsics);
                appliedIntrinsicsVersion = slot.IntrinsicsVersion;
            }

            int actualW = slot.Width;
            int actualH = slot.Height;
            var readback = slot.Readback;

            // The backend converts the readback to grayscale (flipped to a top-left
            // origin) and copies its results into managed TagDetections, so no native
            // result memory outlives this call.
            long detectStart = Stopwatch.GetTimestamp();
            bool converted;
            try
            {
                converted = tagDetector.Detect(
                    readback.Data,
                    readback.ByteCount,
                    actualW,
                    actualH,
                    detections
                );
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError($"{tagDetector.Name} tag detector threw: {ex.Message}");
                Overlay.Clear();
                RecordOutcome(
                    "Detection failed",
                    $"{tagDetector.Name} detector did not return results"
                );
                return;
            }

            // A readback that is not a whole frame is skipped cleanly.
            if (!converted)
            {
                return;
            }

            // Grayscale conversion plus detection, for the status page.
            double detectMilliseconds = ElapsedMilliseconds(detectStart);
            lock (diagnosticsLock)
            {
                detectTime.Add(detectMilliseconds);
            }

            // Apply two filters before counting / solving:
            //   1) User's ignored-IDs blacklist (empty set keeps every detection).
            //   2) Drop detections whose ID is not in the loaded field layout.
            //      tag36h11 readily produces false positives at high resolution
            //      (e.g. lab logs show ID 554 decoding from random texture). Feeding
            //      such an ID to PoseLib mismatches the 2D/3D corner buffer lengths
            //      (8 push, 0 push) and produces a garbage pose tens of meters out,
            //      which the estimator then has to reject as a position jump.
            var kept = new List<TagDetection>(detections.Count);
            int ignoredCount = 0;
            int unknownIdCount = 0;
            var rawIds = new int[detections.Count];
            for (int i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                rawIds[i] = detection.Id;
                var status =
                    slot.IgnoredIds.Contains(detection.Id) ? AprilTagSceneTagStatus.Ignored
                    : aprilTagFieldLayout.ContainsId(detection.Id) ? AprilTagSceneTagStatus.Used
                    : AprilTagSceneTagStatus.Unknown;
                if (status == AprilTagSceneTagStatus.Ignored)
                {
                    ignoredCount++;
                    continue;
                }
                if (status == AprilTagSceneTagStatus.Unknown)
                {
                    unknownIdCount++;
                    continue;
                }
                kept.Add(detection);
            }

            lock (diagnosticsLock)
                detectedIds = rawIds;

            // Publish the robot measurement before single-tag display solves and JPEG encoding.
            // Both views still use these exact detections and this frame's grayscale buffer.
            try
            {
                ProcessMeasurement(slot, kept, rawIds, unknownIdCount, ignoredCount);
            }
            catch (Exception ex)
            {
                // Keep the raw detections visible when localization fails, just as for a
                // rejected pose. Display work uses the original detections, not solve output.
                RecordOutcome("Pose solve failed", ex.Message);
                QueuedLogger.LogError($"AprilTag measurement processing failed: {ex}");
            }
            try
            {
                PublishFrameViews(slot);
            }
            catch (Exception ex)
            {
                // Display failures must not relabel a valid localization result as a detector
                // failure. The measurement has already been published.
                Scene.Clear();
                Overlay.Clear();
                QueuedLogger.LogError($"AprilTag preview processing failed: {ex.Message}");
            }
        }

        private void ProcessMeasurement(
            FrameSlot slot,
            List<TagDetection> kept,
            int[] rawIds,
            int unknownIdCount,
            int ignoredCount
        )
        {
            if (IsStale(slot))
            {
                return;
            }
            if (!aprilTagFieldLayout.IsLoaded)
            {
                RecordOutcome("Field layout unavailable", aprilTagFieldLayout.LoadError);
                return;
            }

            if (kept.Count < slot.MinimumNumberOfTags)
            {
                RecordOutcome(
                    "Insufficient tags",
                    $"Raw IDs [{string.Join(",", rawIds)}], usable {kept.Count}, unknown {unknownIdCount}, "
                        + $"ignored {ignoredCount}, minimum {slot.MinimumNumberOfTags}, layout {aprilTagFieldLayout.LoadedFile}"
                );
                return;
            }

            long solveStart = Stopwatch.GetTimestamp();
            var measurement = Solve(slot, kept, out string outcome, out string reason);
            lock (diagnosticsLock)
            {
                solveTime.Add(ElapsedMilliseconds(solveStart));
            }
            if (measurement == null)
            {
                RecordOutcome(outcome, reason);
                return;
            }
            PublishMeasurement(slot, measurement);
        }

        /// <summary>Optional display work, after the measurement is ready for publication.</summary>
        private void PublishFrameViews(FrameSlot slot)
        {
            if (IsStale(slot))
            {
                return;
            }
            // The viewer lease may have expired while this frame waited for the worker.
            var sceneTags =
                slot.BuildScene && Scene.IsRequested
                    ? new AprilTagSceneTag[detections.Count]
                    : null;
            var overlayTags =
                slot.BuildOverlay && Overlay.IsRequested
                    ? new AprilTagOverlayTag[detections.Count]
                    : null;
            for (int i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                // Keep ignored and unknown tags visible even if this frame had no usable pose.
                if (sceneTags != null)
                {
                    var status =
                        slot.IgnoredIds.Contains(detection.Id) ? AprilTagSceneTagStatus.Ignored
                        : aprilTagFieldLayout.ContainsId(detection.Id) ? AprilTagSceneTagStatus.Used
                        : AprilTagSceneTagStatus.Unknown;
                    sceneTags[i] = BuildSceneTag(detection, status);
                }
                if (overlayTags != null)
                {
                    overlayTags[i] = new AprilTagOverlayTag(
                        detection.Id,
                        ToOverlayPoint(detection.CornerBottomRight0, slot),
                        ToOverlayPoint(detection.CornerBottomLeft1, slot),
                        ToOverlayPoint(detection.CornerUpperLeft2, slot),
                        ToOverlayPoint(detection.CornerUpperRight3, slot)
                    );
                }
            }
            // Disable invalidates the generation, then clears the scene and preview; a frame
            // from before then must not refill them. Both check under their own lock.
            if (sceneTags != null)
            {
                Scene.Publish(
                    new AprilTagSceneFrame(
                        slot.CapturedAtMs,
                        slot.Width,
                        slot.Height,
                        new[]
                        {
                            poseLibSolver.Intrinsics[0],
                            poseLibSolver.Intrinsics[1],
                            poseLibSolver.Intrinsics[2],
                            poseLibSolver.Intrinsics[3],
                        },
                        new Pose3d(
                            Conversions.UnityToFrc3d(
                                slot.LensOffset.position,
                                slot.LensOffset.rotation
                            )
                        ),
                        sceneTags
                    ),
                    () => !IsStale(slot)
                );
            }

            if (overlayTags != null && !IsStale(slot))
            {
                PublishOverlayFrame(tagDetector.Image, slot, overlayTags);
            }
        }

        /// <summary>
        /// Worker thread. Solves the field pose from the usable tags and applies the local
        /// validity checks. Returns null, with the outcome to report, when the frame yields no
        /// measurement.
        /// </summary>
        private AprilTagMeasurement Solve(
            FrameSlot slot,
            List<TagDetection> kept,
            out string outcome,
            out string reason
        )
        {
            outcome = "Measurement rejected locally";
            var poseLibResult = poseLibSolver.PoseLibSolve(kept);

            lock (diagnosticsLock)
            {
                solveCount++;
            }

            if (poseLibResult == null)
            {
                outcome = "Pose solve failed";
                reason = "PoseLib returned no camera pose";
                return null;
            }

            var (frcPos, frcRot) = Conversions.CvToFrc(poseLibResult.CameraPose);
            var measuredRotation = new Rotation3d(
                new Geometry.Quaternion(frcRot.w, frcRot.x, frcRot.y, frcRot.z)
            );

            var cameraPose = new Pose3d(
                new Translation3d(frcPos.x, frcPos.y, frcPos.z),
                measuredRotation
            );
            if (
                !AprilTagPoseValidation.TryValidate(
                    cameraPose,
                    aprilTagFieldLayout.Field,
                    poseLibResult.AcceptedPoints,
                    poseLibResult.TotalPoints,
                    out reason
                )
            )
            {
                return null;
            }
            // Keep the native point counts above for the original robust-fit gate.
            // Every later tag-based check and published observation uses exactly the
            // complete tags that contributed quality and covariance at that pose.
            var accepted = new List<TagDetection>(poseLibResult.AcceptedTagIndices.Count);
            foreach (int index in poseLibResult.AcceptedTagIndices)
            {
                accepted.Add(kept[index]);
            }
            kept = accepted;
            int requiredTags = Math.Max(1, slot.MinimumNumberOfTags);
            if (kept.Count < requiredTags)
            {
                reason =
                    $"Only {kept.Count} complete tag(s) pass the solved pose's reprojection threshold; "
                    + $"require {requiredTags}";
                return null;
            }
            foreach (var detection in kept)
            {
                if (
                    !AprilTagPoseValidation.IsFacingCamera(
                        cameraPose,
                        aprilTagFieldLayout.GetTagPose(detection.Id)
                    )
                )
                {
                    reason =
                        "Tag is behind the solved camera or the camera is behind the printed tag face";
                    return null;
                }
            }
            var quality = poseLibResult.Quality;
            if (!double.IsFinite(quality.ReprojectionErrorPixels))
            {
                reason = "Solved pose places an observed corner behind the camera";
                return null;
            }
            if (poseLibResult.Covariance == null)
            {
                reason = "Pose covariance is undefined for these tag corners";
                return null;
            }
            var headsetPose = AprilTagPoseValidation.CameraToHeadset(cameraPose, slot.LensOffset);
            var headsetCovariance = AprilTagPoseValidation.CameraToHeadset(
                poseLibResult.Covariance,
                slot.LensOffset
            );

            double inlierRatio =
                (poseLibResult.TotalPoints > 0)
                    ? poseLibResult.AcceptedPoints / poseLibResult.TotalPoints
                    : 0.0;

            // avgTagDistance is the mean camera-to-tag distance, not the camera's
            // distance from the field origin, so the maxDistance gate means what
            // the web UI says.
            var observations = BuildObservations(
                kept,
                quality.TagAmbiguities,
                cameraPose,
                headsetPose,
                slot
            );
            double avgTagDistance = 0.0;
            foreach (var observation in observations)
            {
                avgTagDistance += observation.CameraDistanceMeters / observations.Length;
            }

            if (avgTagDistance > slot.MaxDistance)
            {
                reason =
                    $"Average tag distance {avgTagDistance:F2}m exceeds {slot.MaxDistance:F2}m";
                return null;
            }

            var tagIds = new int[kept.Count];
            for (int i = 0; i < kept.Count; i++)
            {
                tagIds[i] = kept[i].Id;
            }

            // Uncertainty comes from Napa's covariance model: the corners' geometry
            // (distance, viewing angle, tag count and spread), widened where the
            // planar mirror solution also fits. See AprilTagCovariance.
            reason = "";
            return new AprilTagMeasurement(
                slot.CaptureTimestampMicros,
                slot.CaptureAppTimestamp,
                headsetPose,
                headsetCovariance,
                tagIds,
                // Native fit diagnostics retain all candidate corners. Tag IDs,
                // quality, distances and observations describe contributing tags.
                (int)poseLibResult.AcceptedPoints,
                poseLibResult.TotalPoints,
                inlierRatio,
                avgTagDistance,
                quality.Ambiguity,
                quality.ReprojectionErrorPixels,
                observations
            );
        }

        private static AprilTagOverlayPoint ToOverlayPoint(Point2D point, FrameSlot slot)
        {
            CameraOrientationMath.RotatePoint(
                slot.Orientation,
                point.x,
                point.y,
                slot.Width,
                slot.Height,
                out double x,
                out double y
            );
            return new AprilTagOverlayPoint(x, y);
        }

        /// <summary>
        /// Solves one detection on its own, in the camera frame. Corners use the same order
        /// the field solve gives PoseLib.
        /// </summary>
        private AprilTagSceneTag BuildSceneTag(
            TagDetection detection,
            AprilTagSceneTagStatus status
        )
        {
            var corners = new[]
            {
                detection.CornerBottomRight0.x,
                detection.CornerBottomRight0.y,
                detection.CornerBottomLeft1.x,
                detection.CornerBottomLeft1.y,
                detection.CornerUpperLeft2.x,
                detection.CornerUpperLeft2.y,
                detection.CornerUpperRight3.x,
                detection.CornerUpperRight3.y,
            };
            if (
                AprilTagCameraPose.TrySolve(
                    corners,
                    aprilTagFieldLayout.TagSize,
                    poseLibSolver.Intrinsics,
                    out var solved
                )
            )
            {
                return new AprilTagSceneTag(
                    detection.Id,
                    status,
                    solved.CameraToTag,
                    solved.Ambiguity,
                    solved.ReprojectionErrorPixels
                );
            }
            return new AprilTagSceneTag(detection.Id, status, null, 1.0, double.NaN);
        }

        /// <summary>
        /// Describes each solved tag in detector order. Pixel coordinates have a top-left origin
        /// (the detectors flip the readback), so "up" is decreasing y. Angles are then rotated to
        /// match the upright image for the configured orientation.
        /// </summary>
        private AprilTagObservation[] BuildObservations(
            List<TagDetection> detections,
            double[] ambiguities,
            Pose3d cameraPose,
            Pose3d headsetPose,
            FrameSlot slot
        )
        {
            var intrinsics = poseLibSolver.Intrinsics;
            double fx = intrinsics[0];
            double fy = intrinsics[1];
            double cx = intrinsics[2];
            double cy = intrinsics[3];
            var observations = new AprilTagObservation[detections.Count];
            for (int i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                var corners = detection.GetCorners();
                double doubledArea = 0;
                for (int c = 0; c < corners.Length; c++)
                {
                    var a = corners[c];
                    var b = corners[(c + 1) % corners.Length];
                    doubledArea += a.x * b.y - b.x * a.y;
                }
                var tagTranslation = aprilTagFieldLayout.GetTagPose(detection.Id).Translation;
                CameraOrientationMath.RotateAngles(
                    slot.Orientation,
                    Math.Atan2(detection.Center.x - cx, fx) * DegreesPerRadian,
                    Math.Atan2(cy - detection.Center.y, fy) * DegreesPerRadian,
                    out double tx,
                    out double ty
                );
                observations[i] = new AprilTagObservation(
                    detection.Id,
                    ambiguities[i],
                    tx,
                    ty,
                    100.0 * Math.Abs(doubledArea) / 2.0 / ((double)slot.Width * slot.Height),
                    tagTranslation.GetDistance(cameraPose.Translation),
                    tagTranslation.GetDistance(headsetPose.Translation)
                );
            }
            return observations;
        }

        private const double DegreesPerRadian = 180.0 / Math.PI;

        private void OnOverlayStreamModeChanged(Config.Config.StreamMode mode)
        {
            overlayQuality = Math.Clamp(mode.Quality, 1, 100);
            overlayFrameIntervalMs = 1000.0 / Math.Max(1, mode.Framerate);
            nextOverlayFrameAt = 0;
            Overlay.Clear();
        }

        private void OnEnableDebugLoggingChanged(bool enabled) => debugLogging = enabled;

        private void OnCameraOrientationChanged(CameraOrientation value)
        {
            orientation = value;
            Overlay.Clear();
        }

        private void OnOverlayStreamEnabledChanged(bool enabled)
        {
            // The worker owns the preview encoder; it is freed when the detector stops.
            Overlay.SetStreamEnabled(enabled);
        }

        /// <summary>
        /// Worker thread. Encodes the exact luma image the detector read, top-down and 1 byte
        /// per pixel, before the detector reuses its buffer for the next frame.
        /// </summary>
        private unsafe void PublishOverlayFrame(
            GrayImage image,
            FrameSlot slot,
            AprilTagOverlayTag[] tags
        )
        {
            if (!image.IsValid)
            {
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                int width = image.Width;
                int height = image.Height;
                overlayCompressor ??= new LJTCompressor();
                byte[] jpeg;
                if (slot.Orientation == CameraOrientation.NORMAL)
                {
                    jpeg = overlayCompressor.Compress(
                        image.Buffer,
                        image.Stride,
                        width,
                        height,
                        LJTPixelFormat.Gray,
                        LJTSubsamplingOption.Gray,
                        slot.OverlayQuality,
                        LJTFlags.None
                    );
                }
                else
                {
                    int bytes = width * height;
                    if (overlayRotationBuffer == null || overlayRotationBuffer.Length != bytes)
                    {
                        overlayRotationBuffer = new byte[bytes];
                    }
                    CameraOrientationMath.RotateSize(
                        slot.Orientation,
                        image.Width,
                        image.Height,
                        out width,
                        out height
                    );
                    fixed (byte* rotated = overlayRotationBuffer)
                    {
                        ImageRotation.Rotate(
                            (byte*)image.Buffer,
                            image.Width,
                            image.Height,
                            image.Stride,
                            1,
                            0,
                            slot.Orientation,
                            rotated
                        );
                        jpeg = overlayCompressor.Compress(
                            (IntPtr)rotated,
                            width,
                            width,
                            height,
                            LJTPixelFormat.Gray,
                            LJTSubsamplingOption.Gray,
                            slot.OverlayQuality,
                            LJTFlags.None
                        );
                    }
                }
                Overlay.Publish(width, height, slot.CapturedAtMs, tags, jpeg);
                lock (diagnosticsLock)
                {
                    previewTime.Add(ElapsedMilliseconds(start));
                }
            }
            catch (Exception ex)
            {
                // Preview failures must not stop pose solving or telemetry.
                Overlay.Clear();
                overlayCompressor?.Dispose();
                overlayCompressor = null;
                lock (diagnosticsLock)
                {
                    overlayRetryAtMs = AprilTagOverlay.MonotonicMilliseconds() + 1000;
                }
                QueuedLogger.LogWarning($"Synchronized AprilTag preview failed: {ex.Message}");
            }
        }
    }
}
