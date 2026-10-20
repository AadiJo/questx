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
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using TagDetectorBackend = QuestX.Config.Config.TagDetectorBackend;

namespace QuestX.QuestX.AprilTag
{
    public class AprilTagManager
    {
        /// <summary>
        /// Stable identifier used by AprilTagManager with the camera arbiter.
        /// </summary>
        private const string CameraArbiterRequesterId = "AprilTagManager";

        /// <summary>
        /// Baseline for the suggested translation uncertainty reported with each
        /// measurement. Robot code may replace this model using the raw quality metrics.
        /// </summary>
        private const double MeasurementLinearStdDevBase = 0.05;

        /// <summary>
        /// Main thread synchronization context for marshalling Unity API calls.
        /// </summary>
        private readonly SynchronizationContext mainThreadContext;

        private readonly AprilTagFieldLayout aprilTagFieldLayout;
        private readonly Func<long> captureTimestampProvider;
        private readonly PassthroughCameraAccess cameraAccess;
        private readonly CameraResourceManager cameraArbiter;
        private readonly IConfigManager configManager;

        // The detector is re-allocated on each enable (and on a backend change while enabled).
        // It is nullable because the disable path tears it down completely.
        private ITagDetector tagDetector;
        private TagDetectorBackend detectorBackend = TagDetectorBackend.NAPA;

        /// <summary>Reused per frame; filled by <see cref="ITagDetector.Detect"/>.</summary>
        private readonly List<TagDetection> detections = new List<TagDetection>();

        private readonly PoseLibSolver poseLibSolver;
        private readonly MonoBehaviour coroutineHost;
        private Coroutine captureCoroutine;
        private float frameDelaySeconds;

        /// <summary>Exposure time, in seconds, of the next frame to process.</summary>
        private double nextFrameExposureSeconds;

        /// <summary>
        /// How early a frame may be exposed and still count as due: a quarter of the 60 Hz
        /// camera period, which absorbs timestamp jitter without taking the frame before.
        /// </summary>
        private const double FrameScheduleToleranceSeconds = 0.004;
        private LJTCompressor overlayCompressor;
        private int overlayQuality = 75;
        private double overlayFrameIntervalMs = 1000.0 / 24;
        private double nextOverlayFrameAt;

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
        /// Rebuilt from <c>Config.AprilTagDetectorMode.IgnoredIds</c> on every config change.
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
        private double lastDetectMilliseconds;
        private double averageDetectMilliseconds;
        private string lastOutcome = "Not started";
        private string lastReason = "";

        /// <summary>
        /// Raised when PoseLib produces a field-relative AprilTag measurement that
        /// passes the Quest's detector-level validity checks. No sensor fusion has
        /// been applied to this measurement.
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
            // Capture main thread context for marshalling Unity API calls
            mainThreadContext = SynchronizationContext.Current;

            this.cameraAccess = cameraAccess;
            this.cameraArbiter = cameraArbiter;
            this.captureTimestampProvider =
                captureTimestampProvider
                ?? throw new ArgumentNullException(nameof(captureTimestampProvider));
            this.configManager = configManager;
            this.aprilTagFieldLayout = aprilTagFieldLayout;
            this.coroutineHost = coroutineHost;
            poseLibSolver = new PoseLibSolver(aprilTagFieldLayout, cameraAccess.Intrinsics);

            configManager.OnEnableAprilTagDetectorChanged += OnEnableAprilTagDetectorChanged;
            configManager.OnAprilTagDetectorModeChanged += OnAprilTagDetectorModeChanged;
            configManager.OnAprilTagDetectorBackendChanged += OnAprilTagDetectorBackendChanged;
            configManager.OnPassthroughStreamModeChanged += OnOverlayStreamModeChanged;
            configManager.OnEnablePassthroughStreamChanged += OnOverlayStreamEnabledChanged;
            configManager.OnCameraOrientationChanged += OnCameraOrientationChanged;

            // Refresh PoseLib's cached intrinsics whenever the camera arbiter changes
            // the effective resolution. Meta SDK exposes per-resolution intrinsics
            // (focal length, principal point, sensor resolution all change with the
            // requested resolution), and PoseLib silently produces a wrong pose if
            // we solve with stale values.
            cameraArbiter.OnResolutionChanged += OnCameraArbiterResolutionChanged;

            QueuedLogger.Log("Initialized AprilTagManager");
        }

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
                if (cameraAccess?.Intrinsics == null || poseLibSolver == null)
                {
                    return false;
                }

                poseLibSolver.RefreshIntrinsics(cameraAccess.Intrinsics);
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

                try
                {
                    SetDetector(CreateDetector(detectorBackend));
                }
                catch (Exception ex)
                {
                    QueuedLogger.LogError($"Failed to allocate tag detector: {ex.Message}");
                    SetDetector(null);
                    return;
                }

                // Reserve the camera at high priority. The arbiter will downgrade the
                // passthrough stream's reservation if it conflicts. Reserve is async
                // (posts to main thread); the coroutine waits for cameraAccess.enabled
                // before calling GetColors so the early frames before the arbiter has
                // applied the reservation are skipped instead of crashing.
                cameraArbiter.Reserve(
                    CameraArbiterRequesterId,
                    currentResolution,
                    CameraRequestPriority.High
                );

                intrinsicsStale = true;
                nextFrameExposureSeconds = 0;
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

                // Flip the active flag FIRST so the coroutine breaks cleanly on its
                // next iteration without touching the detector or family.
                detectorActive = false;
                Overlay.SetEnabled(false);
                Scene.Clear();
                overlayCompressor?.Dispose();
                overlayCompressor = null;

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
                SetDetector(null);
                RecordOutcome("Detector disabled", "");
                QueuedLogger.Log("AprilTagDetector Disabled");
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

        private void SetDetector(ITagDetector detector)
        {
            tagDetector = detector;
            lock (diagnosticsLock)
            {
                detectorName = detector?.Name ?? "";
                lastDetectMilliseconds = 0;
                averageDetectMilliseconds = 0;
            }
        }

        private void OnAprilTagDetectorBackendChanged(TagDetectorBackend backend)
        {
            detectorBackend = backend;
            if (!detectorActive || tagDetector == null)
            {
                return;
            }

            // Config events and the capture coroutine both run on the Unity main thread, so
            // the swap cannot interleave with a Detect call.
            ITagDetector replacement;
            try
            {
                replacement = CreateDetector(backend);
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"Failed to allocate {backend} tag detector; keeping {tagDetector.Name}. {ex.Message}"
                );
                return;
            }

            var previous = tagDetector;
            SetDetector(replacement);
            Overlay.Clear();
            try
            {
                previous.Dispose();
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
            // Rebuild the ignore set on each config change. The capture coroutine reads
            // ignoredIdSet on every frame; replacing the reference atomically is safe
            // because both reads and writes happen on the Unity main thread (the coroutine
            // and this event handler are both main-thread).
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
                    LastDetectMilliseconds = lastDetectMilliseconds,
                    AverageDetectMilliseconds = averageDetectMilliseconds,
                };
            }
        }

        /// <summary>
        /// Paces detection against exposure times instead of sleeping after each frame. The
        /// camera delivers frames on a fixed grid, so 30 Hz takes every second 60 Hz frame;
        /// sleeping a full period after the work instead landed on every second or third
        /// frame (about 23 Hz). The schedule advances by whole periods so rates that don't
        /// divide the camera's (24 Hz) still average out, and re-anchors on this frame after a
        /// stall or a timestamp jump. Returns whether the frame should be processed.
        /// </summary>
        private bool IsFrameDue(double exposureSeconds)
        {
            double period = frameDelaySeconds;
            double early = nextFrameExposureSeconds - exposureSeconds;
            if (early > FrameScheduleToleranceSeconds && early <= period)
            {
                return false;
            }
            nextFrameExposureSeconds =
                (Math.Abs(early) > period ? exposureSeconds : nextFrameExposureSeconds) + period;
            return true;
        }

        private void RecordOutcome(string outcome, string reason)
        {
            lock (diagnosticsLock)
            {
                lastOutcome = outcome;
                lastReason = reason ?? "";
            }
        }

        private IEnumerator AprilTagFrameCaptureCoroutine()
        {
            while (true)
            {
                // Bail out cleanly if the detector was disabled between iterations. Checked
                // BEFORE any cameraAccess / detector access so a torn-down detector cannot
                // be touched on a coroutine that is mid-iteration when disable runs.
                if (!detectorActive || tagDetector == null)
                {
                    yield break;
                }

                // The camera arbiter applies cameraAccess.enabled = true on a Post()-queued
                // main-thread callback, but StartCoroutine runs the first MoveNext()
                // synchronously. If the very first iteration runs before the arbiter has
                // dispatched, the camera is still off; skip the iteration instead of feeding
                // an uninitialized GetColors() result into the native detector.
                if (!cameraAccess.enabled)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                // Skip iterations where the SDK has not yet delivered a new frame this
                // Unity Update. Calling GetColors() anyway would re-trigger an
                // AsyncGPUReadback against the same texture and pile work onto the
                // render thread, which causes the Meta SDK's
                // "MRUK Shared: PCA: previous command buffer is still executing"
                // warning. Detection rate is therefore implicitly capped at the
                // camera's actual delivery rate (~60 Hz on Quest 3), which is what
                // we want.
                if (!cameraAccess.IsUpdatedThisFrame)
                {
                    yield return null;
                    continue;
                }

                // The camera timestamp is the hardware exposure time in UTC. Convert its
                // age into both app-time and NT-time domains so the robot receives the
                // measurement time rather than the later Unity processing time. Clamp the
                // age because the wall clock can step and a bad timestamp should not poison
                // the robot estimator's history buffer.
                double overlayCaptureTime = AprilTagOverlay.MonotonicMilliseconds();
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
                if (!IsFrameDue(cameraAccess.Timestamp.Ticks / (double)TimeSpan.TicksPerSecond))
                {
                    yield return null;
                    continue;
                }
                double captureAppTimestamp = Math.Max(0.0, Time.timeAsDouble - exposureAgeSeconds);
                long captureTimestampMicros =
                    captureTimestampProvider() - (long)Math.Round(exposureAgeSeconds * 1_000_000.0);
                NativeArray<Color32> colors = default;
                string captureFailureReason = null;

                try
                {
                    colors = cameraAccess.GetColors();
                }
                catch (Exception ex)
                {
                    captureFailureReason = ex.Message;
                    lock (diagnosticsLock)
                    {
                        cameraFailureCount++;
                    }
                    RecordOutcome("Camera capture failed", ex.Message);
                    Overlay.Clear();

                    // Camera access can transiently fail after a resolution change, cable
                    // disturbance, or Meta service hiccup. Keep the detector alive and
                    // periodically reassert the arbiter's winning reservation.
                    if (captureAppTimestamp - lastCameraRecoveryAppTimestamp >= 5.0)
                    {
                        lastCameraRecoveryAppTimestamp = captureAppTimestamp;
                        QueuedLogger.LogError(
                            $"AprilTag camera capture failed; retrying and reapplying camera configuration. {ex.Message}"
                        );
                        cameraArbiter.ReapplyCurrentResolution();
                    }
                }

                if (captureFailureReason != null)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                // Validate that we got a frame at all.
                if (!colors.IsCreated || colors.Length == 0)
                {
                    RecordOutcome("Camera frame unavailable", "Camera returned an empty buffer");
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                lock (diagnosticsLock)
                {
                    cameraFrameCount++;
                }

                // The image is CurrentResolution.x by CurrentResolution.y pixels.
                //
                // Note: colors.Length is intentionally NOT used to derive the image
                // size. The Meta SDK over-allocates the readback buffer by 4x (see
                // PassthroughCameraAccess.GetColors at SDK v6d21b459: it allocates
                // CurrentResolution.x * CurrentResolution.y * 4 Color32 elements
                // instead of CurrentResolution.x * CurrentResolution.y). Only the
                // first CurrentResolution.x * CurrentResolution.y elements contain
                // valid image data; the rest is uninitialized GPU memory. Treating
                // the whole buffer as a larger image (e.g. 2560x2560 for a 1280x1280
                // request) causes the AprilTag detector to produce false-positive
                // decodings from the garbage region, which then poison PoseLib with
                // a phantom second tag and tank the RANSAC inlier ratio.
                Vector2Int currentRes = cameraAccess.CurrentResolution;
                int actualW = currentRes.x;
                int actualH = currentRes.y;
                if (actualW <= 0 || actualH <= 0)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }
                if (colors.Length < actualW * actualH)
                {
                    QueuedLogger.LogWarning(
                        $"PassthroughCamera frame buffer too small: colors.Length={colors.Length} "
                            + $"< {actualW}x{actualH}={actualW * actualH}. Skipping frame."
                    );
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

                // The backend converts the readback to grayscale (flipped to a top-left
                // origin) and copies its results into managed TagDetections, so no native
                // result memory outlives this call.
                bool converted = false;
                bool detectFailed = false;
                long detectStart = Stopwatch.GetTimestamp();
                try
                {
                    converted = tagDetector.Detect(colors, actualW, actualH, detections);
                }
                catch (Exception ex)
                {
                    QueuedLogger.LogError($"{tagDetector.Name} tag detector threw: {ex.Message}");
                    detectFailed = true;
                }
                if (detectFailed)
                {
                    Overlay.Clear();
                    RecordOutcome(
                        "Detection failed",
                        $"{tagDetector.Name} detector did not return results"
                    );
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                // A null/empty readback (e.g. the camera was just enabled and hasn't produced
                // a frame yet) is skipped cleanly.
                if (!converted)
                {
                    yield return new WaitForSeconds(frameDelaySeconds);
                    continue;
                }

                // Grayscale conversion plus detection, for the status page.
                double detectMilliseconds =
                    (Stopwatch.GetTimestamp() - detectStart) * 1000.0 / Stopwatch.Frequency;
                lock (diagnosticsLock)
                {
                    lastDetectMilliseconds = detectMilliseconds;
                    averageDetectMilliseconds =
                        averageDetectMilliseconds <= 0
                            ? detectMilliseconds
                            : averageDetectMilliseconds
                                + 0.05 * (detectMilliseconds - averageDetectMilliseconds);
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
                var overlayTags =
                    Overlay.IsRequested
                    && AprilTagOverlay.MonotonicMilliseconds() >= nextOverlayFrameAt
                        ? new AprilTagOverlayTag[detections.Count]
                        : null;
                int overlayIndex = 0;
                int ignoredCount = 0;
                int unknownIdCount = 0;
                var rawIds = new List<int>();
                var sceneTags = Scene.IsRequested
                    ? new List<AprilTagSceneTag>(detections.Count)
                    : null;
                foreach (var detection in detections)
                {
                    rawIds.Add(detection.Id);
                    // Copy raw detections, including ignored/unknown IDs, before pose
                    // filtering. An outline means "detected", not "accepted pose".
                    // The preview encodes the detector's own image, so corners need no flip,
                    // only the same upright rotation as the image.
                    if (overlayTags != null)
                    {
                        overlayTags[overlayIndex++] = new AprilTagOverlayTag(
                            detection.Id,
                            ToOverlayPoint(detection.CornerBottomRight0, actualW, actualH),
                            ToOverlayPoint(detection.CornerBottomLeft1, actualW, actualH),
                            ToOverlayPoint(detection.CornerUpperLeft2, actualW, actualH),
                            ToOverlayPoint(detection.CornerUpperRight3, actualW, actualH)
                        );
                    }
                    var status =
                        ignoredIdSet.Contains(detection.Id) ? AprilTagSceneTagStatus.Ignored
                        : aprilTagFieldLayout.ContainsId(detection.Id) ? AprilTagSceneTagStatus.Used
                        : AprilTagSceneTagStatus.Unknown;
                    sceneTags?.Add(BuildSceneTag(detection, status));
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
                    detectedIds = rawIds.ToArray();

                if (sceneTags != null)
                {
                    var lensOffset = cameraAccess.Intrinsics.LensOffset;
                    Scene.Publish(
                        new AprilTagSceneFrame(
                            overlayCaptureTime - exposureAgeSeconds * 1000.0,
                            actualW,
                            actualH,
                            new[]
                            {
                                poseLibSolver.Intrinsics[0],
                                poseLibSolver.Intrinsics[1],
                                poseLibSolver.Intrinsics[2],
                                poseLibSolver.Intrinsics[3],
                            },
                            new Pose3d(
                                Conversions.UnityToFrc3d(lensOffset.position, lensOffset.rotation)
                            ),
                            sceneTags.ToArray()
                        )
                    );
                }

                if (overlayTags != null)
                {
                    PublishOverlayFrame(
                        tagDetector.Image,
                        overlayCaptureTime - exposureAgeSeconds * 1000.0,
                        overlayTags
                    );
                }

                if (!aprilTagFieldLayout.IsLoaded)
                {
                    RecordOutcome("Field layout unavailable", aprilTagFieldLayout.LoadError);
                    yield return null;
                    continue;
                }

                if (kept.Count >= minimumNumberOfTags)
                {
                    QueuedLogger.Log(
                        $"{kept.Count} usable tag(s) detected "
                            + $"(ignore-set hides {ignoredCount}, "
                            + $"unknown-IDs dropped {unknownIdCount})"
                    );

                    var poseLibResult = poseLibSolver.PoseLibSolve(kept);

                    lock (diagnosticsLock)
                    {
                        solveCount++;
                    }

                    if (poseLibResult != null)
                    {
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
                                out string rejectedReason
                            )
                        )
                        {
                            RecordOutcome("Measurement rejected locally", rejectedReason);
                            yield return null;
                            continue;
                        }
                        bool facesCamera = true;
                        foreach (var detection in kept)
                        {
                            if (
                                !AprilTagPoseValidation.IsFacingCamera(
                                    cameraPose,
                                    aprilTagFieldLayout.GetTagPose(detection.Id)
                                )
                            )
                            {
                                facesCamera = false;
                                break;
                            }
                        }
                        if (!facesCamera)
                        {
                            RecordOutcome(
                                "Measurement rejected locally",
                                "Tag is behind the solved camera or the camera is behind the printed tag face"
                            );
                            yield return null;
                            continue;
                        }
                        var quality = poseLibResult.Quality;
                        if (!double.IsFinite(quality.ReprojectionErrorPixels))
                        {
                            RecordOutcome(
                                "Measurement rejected locally",
                                "Solved pose places an observed corner behind the camera"
                            );
                            yield return null;
                            continue;
                        }
                        var headsetPose = AprilTagPoseValidation.CameraToHeadset(
                            cameraPose,
                            cameraAccess.Intrinsics.LensOffset
                        );

                        int tagCount = kept.Count;
                        double inlierRatio =
                            (poseLibResult.TotalPoints > 0)
                                ? poseLibResult.AcceptedPoints / poseLibResult.TotalPoints
                                : 0.0;

                        // FIX: previously avgTagDistance was ||camera_position||, i.e. the
                        // camera's distance from the FIELD ORIGIN (blue alliance corner).
                        // That value silently inflates the dynamic std dev whenever the camera
                        // is far from the origin and made the user-facing maxDistance gate
                        // behave incorrectly (a robot in the red corner with a tag 1 m away
                        // would compute distance ~16 m). The pipeline appeared to work today
                        // because (a) maxDistance was unenforced and (b) the dynamic std dev
                        // only affects correction trust, not pass/fail. Switching to the true
                        // mean camera-to-tag distance changes the std dev curve - if pose
                        // behavior regresses after this change, suspect this block first.
                        var observations = BuildObservations(
                            kept,
                            quality.TagAmbiguities,
                            cameraPose,
                            headsetPose,
                            actualW,
                            actualH
                        );
                        double avgTagDistance = 0.0;
                        foreach (var observation in observations)
                        {
                            avgTagDistance +=
                                observation.CameraDistanceMeters / observations.Length;
                        }

                        if (avgTagDistance > maxDistance)
                        {
                            RecordOutcome(
                                "Measurement rejected locally",
                                $"Average tag distance {avgTagDistance:F2}m exceeds {maxDistance:F2}m"
                            );
                            QueuedLogger.Log(
                                $"AprilTag observation rejected: avgTagDistance={avgTagDistance:F2}m "
                                    + $"> maxDistance={maxDistance:F2}m"
                            );
                            yield return null;
                            continue;
                        }

                        // Dynamic std devs: uncertainty scales with distance^2 and decreases with tag count.
                        // Suggested uncertainty is derived only from observable geometry.
                        // Robot code remains the sole owner of estimator trust/tuning.
                        double stdDevFactor =
                            (avgTagDistance * avgTagDistance) / Math.Max(1, tagCount);
                        double linearStdDev = MeasurementLinearStdDevBase * stdDevFactor;
                        // This is a suggested uncertainty model, not a covariance reported
                        // by the Meta or AprilTag SDK. Raw quality metrics are published beside
                        // it so robot code can replace the model without changing the Quest app.
                        double angularStdDev =
                            MeasurementLinearStdDevBase
                            * avgTagDistance
                            / Math.Sqrt(Math.Max(1, tagCount));
                        // A single planar tag has a weaker/ambiguous heading solution. Do not
                        // advertise unrealistically precise yaw to the robot's estimator.
                        angularStdDev = Math.Max(tagCount == 1 ? 0.25 : 0.03, angularStdDev);
                        linearStdDev = Math.Max(tagCount == 1 ? 0.08 : 0.02, linearStdDev);

                        var tagIds = new int[kept.Count];
                        for (int i = 0; i < kept.Count; i++)
                        {
                            tagIds[i] = kept[i].Id;
                        }

                        var measurement = new AprilTagMeasurement(
                            captureTimestampMicros,
                            captureAppTimestamp,
                            headsetPose,
                            tagIds,
                            (int)poseLibResult.AcceptedPoints,
                            poseLibResult.TotalPoints,
                            inlierRatio,
                            avgTagDistance,
                            linearStdDev,
                            linearStdDev,
                            linearStdDev * 2.0,
                            angularStdDev * 2.0,
                            angularStdDev * 2.0,
                            angularStdDev,
                            quality.Ambiguity,
                            quality.ReprojectionErrorPixels,
                            observations
                        );

                        try
                        {
                            MeasurementAvailable?.Invoke(measurement);
                            lock (diagnosticsLock)
                            {
                                measurementCount++;
                                lastMeasurementAppTimestamp = captureAppTimestamp;
                                lastTagIds = tagIds;
                                lastInlierCount = measurement.InlierCount;
                                lastCornerCount = measurement.CornerCount;
                                lastInlierRatio = inlierRatio;
                                lastAverageTagDistanceMeters = avgTagDistance;
                                lastLinearStdDevMeters = linearStdDev;
                                lastAmbiguity = quality.Ambiguity;
                                lastReprojectionErrorPixels = quality.ReprojectionErrorPixels;
                                lastOutcome = "Measurement published";
                                lastReason = "";
                            }

                            QueuedLogger.Log(
                                $"AprilTag measurement published: Pos({frcPos.x:F3}, {frcPos.y:F3}, {frcPos.z:F3}) "
                                    + $"tags={tagCount} inliers={poseLibResult.AcceptedPoints}/{poseLibResult.TotalPoints} "
                                    + $"ratio={inlierRatio:F2} dist={avgTagDistance:F2}m stdDev={linearStdDev:F4} "
                                    + $"ambiguity={quality.Ambiguity:F3} reprojection={quality.ReprojectionErrorPixels:F2}px"
                            );
                        }
                        catch (Exception ex)
                        {
                            RecordOutcome("Measurement publish failed", ex.Message);
                            QueuedLogger.LogError(
                                $"AprilTag measurement subscriber threw: {ex.Message}"
                            );
                        }
                    }
                    else
                    {
                        RecordOutcome("Pose solve failed", "PoseLib returned no camera pose");
                    }
                }
                else
                {
                    RecordOutcome(
                        "Insufficient tags",
                        $"Raw IDs [{string.Join(",", rawIds)}], usable {kept.Count}, unknown {unknownIdCount}, "
                            + $"ignored {ignoredCount}, minimum {minimumNumberOfTags}, layout {aprilTagFieldLayout.LoadedFile}"
                    );
                }

                yield return null;
            }
        }

        private AprilTagOverlayPoint ToOverlayPoint(Point2D point, int width, int height)
        {
            CameraOrientationMath.RotatePoint(
                orientation,
                point.x,
                point.y,
                width,
                height,
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
        /// (ImageU8 flips the readback), so "up" is decreasing y. Angles are then rotated to
        /// match the upright image for the configured orientation.
        /// </summary>
        private AprilTagObservation[] BuildObservations(
            List<TagDetection> detections,
            double[] ambiguities,
            Pose3d cameraPose,
            Pose3d headsetPose,
            int imageWidth,
            int imageHeight
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
                    orientation,
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
                    100.0 * Math.Abs(doubledArea) / 2.0 / ((double)imageWidth * imageHeight),
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
                overlayCompressor?.Dispose();
                overlayCompressor = null;
            }
        }

        private unsafe void PublishOverlayFrame(
            GrayImage image,
            double capturedAt,
            AprilTagOverlayTag[] tags
        )
        {
            // Encode the exact luma image the detector read, top-down and 1 byte per
            // pixel, before any coroutine yield: the detector reuses the buffer next frame.
            nextOverlayFrameAt = AprilTagOverlay.MonotonicMilliseconds() + overlayFrameIntervalMs;
            if (!image.IsValid)
            {
                return;
            }
            try
            {
                int width = image.Width;
                int height = image.Height;
                overlayCompressor ??= new LJTCompressor();
                byte[] jpeg;
                if (orientation == CameraOrientation.NORMAL)
                {
                    jpeg = overlayCompressor.Compress(
                        image.Buffer,
                        image.Stride,
                        width,
                        height,
                        LJTPixelFormat.Gray,
                        LJTSubsamplingOption.Gray,
                        overlayQuality,
                        LJTFlags.None
                    );
                }
                else
                {
                    var source = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(
                        (void*)image.Buffer,
                        height * image.Stride,
                        Allocator.None
                    );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(
                        ref source,
                        AtomicSafetyHandle.GetTempMemoryHandle()
                    );
#endif
                    using var rotated = ImageRotationJob.Rotate(
                        source,
                        width,
                        height,
                        image.Stride,
                        1,
                        sourceBottomUp: false,
                        orientation,
                        Allocator.TempJob
                    );
                    CameraOrientationMath.RotateSize(
                        orientation,
                        image.Width,
                        image.Height,
                        out width,
                        out height
                    );
                    jpeg = overlayCompressor.Compress(
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rotated),
                        width,
                        width,
                        height,
                        LJTPixelFormat.Gray,
                        LJTSubsamplingOption.Gray,
                        overlayQuality,
                        LJTFlags.None
                    );
                }
                Overlay.Publish(width, height, capturedAt, tags, jpeg);
            }
            catch (Exception ex)
            {
                // Preview failures must not stop pose solving or telemetry.
                Overlay.Clear();
                overlayCompressor?.Dispose();
                overlayCompressor = null;
                nextOverlayFrameAt = AprilTagOverlay.MonotonicMilliseconds() + 1000;
                QueuedLogger.LogWarning($"Synchronized AprilTag preview failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invokes an action on the main thread using the captured SynchronizationContext.
        /// Falls back to direct invocation if no context was captured.
        /// </summary>
        private void InvokeOnMainThread(Action action)
        {
            if (mainThreadContext == null)
            {
                action();
            }
            else
            {
                mainThreadContext.Post(_ => action(), null);
            }
        }
    }
}
