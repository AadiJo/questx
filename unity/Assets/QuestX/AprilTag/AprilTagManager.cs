using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LibJpegTurboUnity;
using Meta.XR;
using QuestX.Camera;
using QuestX.Config;
using QuestX.Core;
using QuestX.Native.AprilTag;
using QuestX.Native.Camera;
using QuestX.Native.Napa;
using QuestX.Native.PoseLib;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using UnityEngine;
using TagDetectorBackend = QuestX.Config.Config.TagDetectorBackend;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Runs AprilTag localization off the Unity main thread. Frames normally come from QuestX's
    /// own Camera2 client as grayscale, straight to a capture thread that detects, solves and
    /// publishes each one as it arrives. If that camera cannot start, Meta's camera access is
    /// used instead: the main thread decides which frames to use and starts a non-blocking GPU
    /// readback of each, and a dedicated worker converts, detects, solves and publishes. Either
    /// way rendering never waits on the camera or the detector, detection runs at the camera's
    /// rate, and the robot's sample does not wait for the next Unity frame.
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

        private CaptureBackend activeCapture = CaptureBackend.Mruk;

        /// <summary>Set when native capture cannot start; Meta's access is used until restart.</summary>
        private bool nativeUnavailable;
        private NativeCaptureSession nativeSession;
        private readonly string captureOverridePath;
        private double nextCaptureOverrideCheck;
        private int configuredFramerate = 60;

        /// <summary>
        /// <see cref="Time.timeAsDouble"/> and the stopwatch at the same instant, refreshed every
        /// frame, so the native capture thread can stamp measurements in app time.
        /// </summary>
        private readonly object appClockLock = new object();
        private double appClockTime;
        private long appClockTicks;

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

        /// <summary>
        /// Scales and encodes the preview on its own low-priority thread. It keeps only the newest
        /// frame, so a slow encode or viewer drops preview frames instead of delaying detection.
        /// </summary>
        private readonly AprilTagPreviewEncoder previewEncoder;

        private int overlayQuality = 75;

        /// <summary>
        /// Preview scale-down per side from the settings (default a quarter, about the size of a
        /// Limelight stream); larger than 640x480 only with high-quality streams.
        /// </summary>
        private volatile int previewScale = 4;
        private volatile bool highQualityStreams;
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

        /// <summary>Native capture: exposure until the frame reached the capture thread.</summary>
        private RunningAverage deliveryTime;
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
        private long replacedFrameCount;
        private long nativeOverwrittenFrameCount;
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
            captureOverridePath = Path.Combine(
                Application.persistentDataPath,
                "capture-backend.txt"
            );
            UpdateAppClock();
            previewEncoder = new AprilTagPreviewEncoder(
                () => new LjtGrayEncoder(),
                Overlay.Publish,
                ex =>
                {
                    // Preview failures must not stop pose solving or telemetry.
                    Overlay.Clear();
                    lock (diagnosticsLock)
                    {
                        overlayRetryAtMs = AprilTagOverlay.MonotonicMilliseconds() + 1000;
                    }
                    QueuedLogger.LogWarning($"Synchronized AprilTag preview failed: {ex.Message}");
                },
                milliseconds =>
                {
                    lock (diagnosticsLock)
                    {
                        previewTime.Add(milliseconds);
                    }
                }
            );
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
            configManager.OnPreviewScaleChanged += scale =>
            {
                previewScale = scale;
                Overlay.Clear();
            };
            configManager.OnEnableHighQualityStreamsChanged += enabled =>
            {
                highQualityStreams = enabled;
                Overlay.Clear();
            };

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

            /// <summary>
            /// Native capture: the camera's top-left-origin luma, valid while the capture thread
            /// processes this frame. Zero for a Meta readback.
            /// </summary>
            public IntPtr Gray;
            public int GrayStride;
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

                intrinsicsStale = true;
                nextFrameExposureSeconds = 0;
                nextMeasurementLogAtMs = 0;
                int generation = Interlocked.Increment(ref pipelineGeneration);
                requestedBackend = (int)detectorBackend;
                detectorActive = true;
                Overlay.SetEnabled(true);
                lock (diagnosticsLock)
                {
                    deliveryTime = default;
                    readbackTime = default;
                    latency = default;
                }
                nextCaptureOverrideCheck = Time.realtimeSinceStartupAsDouble + 1.0;
                UpdateAppClock();
                if (PreferredCapture() == CaptureBackend.Native)
                {
                    // QuestX's camera client takes the left camera, so Meta's access moves to the
                    // right one if the passthrough stream wants it. The supervisor starts the
                    // camera once Meta's access has let go of the left one.
                    activeCapture = CaptureBackend.Native;
                    cameraArbiter.SetCameraPosition(
                        PassthroughCameraAccess.CameraPositionType.Right
                    );
                    captureCoroutine = coroutineHost.StartCoroutine(
                        NativeCaptureSupervisor(generation)
                    );
                }
                else
                {
                    StartMrukCapture();
                }
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
                cameraArbiter.SetCameraPosition(PassthroughCameraAccess.CameraPositionType.Left);
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
            previewEncoder.Dispose();
        }

        /// <summary>
        /// Stops the worker or native capture thread after its current frame, then frees the
        /// detector, the preview encoder and the frame buffers. If the thread is stuck in native
        /// code, its resources are left allocated rather than freed underneath it, and a later
        /// call retries. Returns whether no worker remains.
        /// </summary>
        private bool StopWorker(TimeSpan timeout)
        {
            if (!StopNativeCapture(timeout))
            {
                return false;
            }
            if (worker == null)
            {
                ReleaseWorkerResources();
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
            lock (diagnosticsLock)
            {
                replacedFrameCount += worker.ReplacedCount;
                worker = null;
            }
            ReleaseWorkerResources();
            return true;
        }

        private void ReleaseWorkerResources()
        {
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
            previewEncoder.Reset();

            lock (slotLock)
            {
                foreach (var slot in frameSlots)
                {
                    // Waits for any copy still in flight, since the GPU writes into the buffer.
                    slot.Readback.Dispose();
                    slot.State = SlotState.Free;
                }
            }
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
            bool framerateChanged = configuration.Framerate != configuredFramerate;
            configuredFramerate = configuration.Framerate;

            // Only ask the arbiter to re-apply when we are currently the active high-priority
            // requester AND the resolution actually changed. A pure FPS change does not need
            // to bounce the camera (the framerate is enforced by IsFrameDue).
            if (
                activeCapture == CaptureBackend.Native
                && detectorActive
                && (resolutionChanged || framerateChanged)
            )
            {
                // The supervisor reopens the camera in the new mode (its auto exposure is
                // locked to the frame rate).
                StopNativeCapture(WorkerStopTimeout);
            }
            else if (resolutionChanged && detectorActive)
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
                    CaptureBackend = detectorActive ? activeCapture.ToString() : "",
                    AverageDeliveryMilliseconds = deliveryTime.Average,
                    AverageSolveMilliseconds = solveTime.Average,
                    AveragePreviewMilliseconds = previewTime.Average,
                    AverageLatencyMilliseconds = latency.Average,
                    CameraFramesPerSecond = cameraFramesPerSecond,
                    ProcessedFramesPerSecond = processedFramesPerSecond,
                    MeasurementsPerSecond = measurementsPerSecond,
                    BusyFrameCount = busyFrameCount,
                    ReplacedFrameCount = replacedFrameCount + (worker?.ReplacedCount ?? 0),
                    NativeOverwrittenFrameCount = Interlocked.Read(ref nativeOverwrittenFrameCount),
                    PreviewReplacedFrameCount = previewEncoder.DroppedCount,
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

        private IEnumerator AprilTagFrameCaptureCoroutine(int generation)
        {
            while (true)
            {
                UpdateRates();

                // Bail out cleanly if the detector was disabled or restarted between iterations.
                if (
                    !detectorActive
                    || worker == null
                    || generation != Volatile.Read(ref pipelineGeneration)
                )
                {
                    yield break;
                }
                if (SwitchCaptureIfRequested())
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
                Interlocked.Increment(ref cameraUpdateCount);

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
            long cameraUpdates = Interlocked.Read(ref cameraUpdateCount);
            lock (diagnosticsLock)
            {
                cameraFramesPerSecond = (cameraUpdates - rateWindowCameraUpdates) / seconds;
                processedFramesPerSecond = (processed - rateWindowProcessed) / seconds;
                measurementsPerSecond = (measurementCount - rateWindowMeasurements) / seconds;
                rateWindowMeasurements = measurementCount;
            }
            rateWindowCameraUpdates = cameraUpdates;
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
            bool converted = true;
            try
            {
                if (slot.Gray != IntPtr.Zero)
                {
                    tagDetector.DetectGray(
                        slot.Gray,
                        actualW,
                        actualH,
                        slot.GrayStride,
                        detections
                    );
                }
                else
                {
                    converted = tagDetector.Detect(
                        readback.Data,
                        readback.ByteCount,
                        actualW,
                        actualH,
                        detections
                    );
                }
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

        // ---- Native capture ----------------------------------------------------------------

        /// <summary>
        /// Where frames come from. Native reads luma from QuestX's own Camera2 client on a
        /// dedicated thread that detects, solves and publishes each frame the moment it arrives,
        /// with no GPU copy and no wait for a Unity frame. Mruk is Meta's PassthroughCameraAccess
        /// with a GPU readback and the detection worker, kept as the fallback.
        /// </summary>
        private enum CaptureBackend
        {
            Native,
            Mruk,
        }

        /// <summary>
        /// One open native camera and the thread reading it. The thread checks and reports through
        /// its own session, so a late thread can never stop or fail a newer one.
        /// </summary>
        private sealed class NativeCaptureSession
        {
            public readonly NativePassthroughCamera Camera;
            public readonly int IntrinsicsVersion;
            public readonly FrameSlot Slot = new FrameSlot();

            /// <summary>Camera updates counted before this session; the camera counts from 0.</summary>
            public long DeliveredBase;
            public long OverwrittenBase;
            public Thread Thread;
            public volatile bool Running;
            public volatile int Failure;

            public NativeCaptureSession(NativePassthroughCamera camera, int intrinsicsVersion)
            {
                Camera = camera;
                IntrinsicsVersion = intrinsicsVersion;
            }
        }

        private void StartMrukCapture()
        {
            activeCapture = CaptureBackend.Mruk;
            cameraArbiter.SetCameraPosition(PassthroughCameraAccess.CameraPositionType.Left);
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
            worker = new LatestOnlyWorker<FrameSlot>(
                "AprilTag detection",
                ProcessFrameOnWorker,
                ReleaseSlot
            );
            captureCoroutine = coroutineHost.StartCoroutine(
                AprilTagFrameCaptureCoroutine(Volatile.Read(ref pipelineGeneration))
            );
        }

        /// <summary>
        /// Keeps the native camera running for as long as this enable lasts: starts it once Meta's
        /// access has let go of the left camera, restarts it after a device error or a mode
        /// change, and falls back to Meta's access for the rest of the session if it cannot start.
        /// Also refreshes the app clock the capture thread reads, and the rates.
        /// </summary>
        private IEnumerator NativeCaptureSupervisor(int generation)
        {
            // StartCoroutine runs the first step synchronously; start nothing until it returns,
            // so a fallback's coroutine handle is not overwritten by this one.
            yield return null;
            bool everStarted = false;
            int failedStarts = 0;
            double retryAt = 0;
            double waitingSince = Time.realtimeSinceStartupAsDouble;
            while (detectorActive && generation == Volatile.Read(ref pipelineGeneration))
            {
                UpdateAppClock();
                UpdateRates();
                if (SwitchCaptureIfRequested())
                {
                    yield break;
                }
                double now = Time.realtimeSinceStartupAsDouble;
                var session = nativeSession;
                if (session != null && session.Failure != 0)
                {
                    QueuedLogger.LogWarning(
                        $"AprilTag camera stopped (status {session.Failure}); restarting it"
                    );
                    lock (diagnosticsLock)
                    {
                        cameraFailureCount++;
                    }
                    RecordOutcome(
                        "Camera capture failed",
                        $"Native camera status {session.Failure}"
                    );
                    StopNativeCapture(WorkerStopTimeout);
                    retryAt = now + 1.0;
                }

                bool leftCameraFree = !cameraArbiter.IsUsingCamera(
                    PassthroughCameraAccess.CameraPositionType.Left
                );
                string fallbackReason = null;
                if (nativeSession == null && now >= retryAt && leftCameraFree)
                {
                    try
                    {
                        StartNativeCapture();
                        everStarted = true;
                        failedStarts = 0;
                    }
                    catch (Exception ex)
                        when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                    {
                        fallbackReason = ex.Message;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Opening can fail briefly, e.g. right after Meta's access closed the
                        // camera or before the camera permission is granted.
                        RecordOutcome("Camera capture failed", ex.Message);
                        retryAt = now + 1.0;
                        if (!everStarted && ++failedStarts >= 3)
                        {
                            fallbackReason = ex.Message;
                        }
                    }
                }
                else if (nativeSession == null && !everStarted && now - waitingSince > 5.0)
                {
                    fallbackReason = "Meta's camera access did not release the left camera";
                }
                if (fallbackReason != null)
                {
                    QueuedLogger.LogError(
                        $"Native passthrough camera unavailable ({fallbackReason}); using Meta's camera access until the app restarts."
                    );
                    nativeUnavailable = true;
                    StartMrukCapture();
                    yield break;
                }
                yield return null;
            }
        }

        private void StartNativeCapture()
        {
            var camera = NativePassthroughCamera.Start(
                PassthroughCameraAccess.CameraPositionType.Left,
                currentResolution,
                configuredFramerate
            );
            var intrinsics = camera.Intrinsics;
            cachedIntrinsics = intrinsics;
            cachedIntrinsicsVersion++;
            cachedIntrinsicsResolution = currentResolution;
            intrinsicsStale = false;
            nextFrameExposureSeconds = 0;
            nativeSession = new NativeCaptureSession(camera, cachedIntrinsicsVersion)
            {
                DeliveredBase = Interlocked.Read(ref cameraUpdateCount),
                OverwrittenBase = Interlocked.Read(ref nativeOverwrittenFrameCount),
            };
            previewEncoder.Reset();
            Overlay.Clear();
            Scene.Clear();
            nativeSession.Running = true;
            nativeSession.Thread = new Thread(NativeCaptureLoop)
            {
                IsBackground = true,
                Name = "AprilTag native capture",
                Priority = System.Threading.ThreadPriority.Highest,
            };
            nativeSession.Thread.Start(nativeSession);
            QueuedLogger.Log(
                $"AprilTag camera {camera.CameraId} streaming {camera.Resolution.x}x{camera.Resolution.y} at {camera.Fps} fps "
                    + $"(f=({intrinsics.FocalLength.x:F2}, {intrinsics.FocalLength.y:F2}) c=({intrinsics.PrincipalPoint.x:F2}, {intrinsics.PrincipalPoint.y:F2}))"
            );
        }

        /// <summary>
        /// Stops the capture thread, then closes the camera. Returns false, leaving both alone,
        /// if the thread is still inside a frame after <paramref name="timeout"/>: its frame
        /// buffer and the detector must outlive it. A later call retries.
        /// </summary>
        private bool StopNativeCapture(TimeSpan timeout)
        {
            var session = nativeSession;
            if (session == null)
            {
                return true;
            }
            session.Running = false;
            if (session.Thread != null && !session.Thread.Join(timeout))
            {
                if (timeout > TimeSpan.Zero)
                {
                    QueuedLogger.LogError(
                        $"AprilTag native capture thread did not stop within {timeout.TotalSeconds:F0} s; "
                            + "leaving the camera and detector allocated."
                    );
                }
                return false;
            }
            session.Camera.Dispose();
            nativeSession = null;
            return true;
        }

        /// <summary>
        /// The native capture thread: waits on the camera, then detects, solves and publishes
        /// each frame as soon as it arrives. It owns the detector while it runs.
        /// </summary>
        private void NativeCaptureLoop(object state)
        {
            var session = (NativeCaptureSession)state;
            var camera = session.Camera;
            while (session.Running)
            {
                int status = camera.Acquire(250, out QxFrame frame);
                if (status == 0)
                {
                    continue;
                }
                if (status < 0)
                {
                    session.Failure = status;
                    break;
                }
                var stats = camera.Stats;
                Interlocked.Exchange(
                    ref cameraUpdateCount,
                    session.DeliveredBase + stats.delivered
                );
                Interlocked.Exchange(
                    ref nativeOverwrittenFrameCount,
                    session.OverwrittenBase + stats.overwritten
                );
                ProcessNativeFrame(session, frame);
            }
            camera.Release();
        }

        private void ProcessNativeFrame(NativeCaptureSession session, QxFrame frame)
        {
            // While Unity is paused or stalled its clock stands still, and Meta's capture would
            // not run either; skip frames rather than stamp them with an extrapolated app time.
            if (!TryAppNow(out double appNow))
            {
                RecordOutcome("Waiting for app", "Unity is paused");
                return;
            }
            long nowUnixNs = (DateTime.UtcNow.Ticks - UnixEpochTicks) * 100;
            double exposureAgeSeconds = (nowUnixNs - frame.exposure_unix_ns) / 1e9;
            if (exposureAgeSeconds < -0.01 || exposureAgeSeconds > 0.5)
            {
                RecordOutcome("Camera timestamp invalid", $"Exposure age {exposureAgeSeconds:F3}s");
                return;
            }
            exposureAgeSeconds = Math.Max(0.0, exposureAgeSeconds);
            double exposureSeconds = frame.exposure_unix_ns / 1e9;
            if (!IsFrameDue(exposureSeconds))
            {
                return;
            }
            TakeFrame(exposureSeconds);
            lock (diagnosticsLock)
            {
                cameraFrameCount++;
                deliveryTime.Add((frame.arrival_unix_ns - frame.exposure_unix_ns) / 1e6);
            }

            var slot = session.Slot;
            slot.Gray = frame.data;
            slot.GrayStride = frame.stride;
            slot.Generation = Volatile.Read(ref pipelineGeneration);
            slot.RequestedAt = Stopwatch.GetTimestamp();
            slot.ExposureUtcTicks = frame.exposure_unix_ns / 100 + UnixEpochTicks;
            slot.CaptureTimestampMicros =
                captureTimestampProvider() - (long)Math.Round(exposureAgeSeconds * 1_000_000.0);
            slot.CaptureAppTimestamp = Math.Max(0.0, appNow - exposureAgeSeconds);
            double now = AprilTagOverlay.MonotonicMilliseconds();
            slot.CapturedAtMs = now - exposureAgeSeconds * 1000.0;
            slot.Width = frame.width;
            slot.Height = frame.height;
            slot.Intrinsics = session.Camera.Intrinsics;
            slot.IntrinsicsVersion = session.IntrinsicsVersion;
            slot.LensOffset = session.Camera.Intrinsics.LensOffset;
            slot.IgnoredIds = ignoredIdSet;
            slot.MaxDistance = maxDistance;
            slot.MinimumNumberOfTags = minimumNumberOfTags;
            slot.Orientation = orientation;
            slot.BuildScene = Scene.IsRequested;
            slot.OverlayQuality = overlayQuality;
            double overlayRetryAt;
            lock (diagnosticsLock)
            {
                overlayRetryAt = overlayRetryAtMs;
            }
            slot.BuildOverlay =
                Overlay.IsRequested
                && now >= overlayRetryAt
                && FrameCadence.TryTake(now, overlayFrameIntervalMs, ref nextOverlayFrameAt);
            ProcessFrameOnWorker(slot);
        }

        private const long UnixEpochTicks = 621355968000000000L;

        /// <summary>
        /// Reads the capture override (<c>capture-backend.txt</c> in the app's files, containing
        /// <c>native</c> or <c>mruk</c>), so the two paths can be compared on a headset without a
        /// rebuild. Null when there is no override.
        /// </summary>
        private CaptureBackend? ReadCaptureOverride()
        {
            try
            {
                if (File.Exists(captureOverridePath))
                {
                    switch (File.ReadAllText(captureOverridePath).Trim().ToLowerInvariant())
                    {
                        case "mruk":
                            return CaptureBackend.Mruk;
                        case "native":
                            return CaptureBackend.Native;
                    }
                }
            }
            catch (Exception ex)
            {
                QueuedLogger.LogWarning($"Could not read {captureOverridePath}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Native unless the override asks for Meta's access, or native capture already failed
        /// this session.
        /// </summary>
        private CaptureBackend PreferredCapture() =>
            nativeUnavailable
                ? CaptureBackend.Mruk
                : ReadCaptureOverride() ?? CaptureBackend.Native;

        /// <summary>
        /// Main thread, about once a second: restarts the detector if the override names the
        /// other path. Returns true when a restart is queued and the calling coroutine should end.
        /// </summary>
        private bool SwitchCaptureIfRequested()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextCaptureOverrideCheck)
            {
                return false;
            }
            nextCaptureOverrideCheck = now + 1.0;
            var preferred = PreferredCapture();
            if (preferred == activeCapture)
            {
                return false;
            }
            QueuedLogger.Log($"Switching AprilTag capture from {activeCapture} to {preferred}");
            coroutineHost.StartCoroutine(RestartDetectorNextFrame());
            return true;
        }

        private IEnumerator RestartDetectorNextFrame()
        {
            yield return null;
            if (!detectorActive)
            {
                yield break;
            }
            OnEnableAprilTagDetectorChanged(false);
            OnEnableAprilTagDetectorChanged(true);
        }

        private void UpdateAppClock()
        {
            lock (appClockLock)
            {
                appClockTime = Time.timeAsDouble;
                appClockTicks = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// <see cref="Time.timeAsDouble"/> now, from any thread. False if Unity has not refreshed
        /// the anchor for 250 ms (paused or stalled).
        /// </summary>
        private bool TryAppNow(out double now)
        {
            lock (appClockLock)
            {
                double elapsed =
                    (Stopwatch.GetTimestamp() - appClockTicks) / (double)Stopwatch.Frequency;
                now = appClockTime + elapsed;
                return elapsed < 0.25;
            }
        }

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
            Overlay.SetStreamEnabled(enabled);
            if (!enabled)
            {
                previewEncoder.Reset();
            }
        }

        /// <summary>
        /// Worker thread. Hands the luma image the detector read to the preview encoder, which
        /// copies it before returning, so the detector may reuse its buffer for the next frame and
        /// detection never waits on scaling or JPEG work.
        /// </summary>
        private void PublishOverlayFrame(GrayImage image, FrameSlot slot, AprilTagOverlayTag[] tags)
        {
            if (!image.IsValid)
            {
                return;
            }
            previewEncoder.Submit(
                image.Buffer,
                image.Width,
                image.Height,
                image.Stride,
                slot.CapturedAtMs,
                tags,
                slot.Orientation,
                slot.OverlayQuality,
                AprilTagPreviewEncoder.ScaleFactor(
                    image.Width,
                    image.Height,
                    previewScale,
                    highQualityStreams ? 0 : AprilTagPreviewEncoder.StandardMaxPixels
                )
            );
        }

        private sealed class LjtGrayEncoder : IGrayJpegEncoder
        {
            private readonly LJTCompressor compressor = new LJTCompressor();

            public byte[] Encode(IntPtr pixels, int stride, int width, int height, int quality) =>
                compressor.Compress(
                    pixels,
                    stride,
                    width,
                    height,
                    LJTPixelFormat.Gray,
                    LJTSubsamplingOption.Gray,
                    quality,
                    LJTFlags.None
                );

            public void Dispose() => compressor.Dispose();
        }
    }
}
