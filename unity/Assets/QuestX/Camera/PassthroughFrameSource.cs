using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using LibJpegTurboUnity;
using Meta.XR;
using QuestX.Config;
using QuestX.Network;
using QuestX.Utils;
using QuestX.WebServer;
using UnityEngine;
using static QuestX.Config.Config;
using static QuestX.Core.QuestXConstants;

namespace QuestX.Camera
{
    /// <summary>
    /// Handles capturing frames from PassthroughCameraAccess and encoding them for streaming.
    /// Extracted from VideoStreamProvider.
    /// </summary>
    public class PassthroughFrameSource : VideoStreamProvider.IFrameSource
    {
        /// <summary>
        /// Available FPS options for video streaming. Sourced from
        /// <c>QuestXConstants.VideoStream.SUPPORTED_FPS</c> (imported via the
        /// file-level <c>using static</c> directive) so the Camera tab dropdown and
        /// the AprilTag tab dropdown stay in lock-step.
        /// </summary>
        private static readonly int[] FpsOptions = VideoStream.SUPPORTED_FPS;

        /// <summary>
        /// Time without an encoded frame before asking the camera arbiter to reapply its
        /// winning reservation.
        /// </summary>
        private const long CameraRecoveryIntervalMs = 5000;

        /// <summary>
        /// MonoBehaviour host for running coroutines.
        /// </summary>
        private readonly MonoBehaviour coroutineHost;

        /// <summary>
        /// Meta SDK passthrough camera accessor. Used for frame reads (GetTexture);
        /// the camera's enable state and resolution are owned by <see cref="cameraArbiter"/>.
        /// </summary>
        private readonly PassthroughCameraAccess cameraAccess;

        /// <summary>
        /// Single owner of camera enable/resolution. PFS reserves at low priority;
        /// the AprilTag detector reserves at high priority and wins ties.
        /// </summary>
        private readonly CameraResourceManager cameraArbiter;

        /// <summary>
        /// Stable identifier used by this PFS instance with the arbiter. Multiple PFS
        /// instances (not currently expected) would each need a distinct id.
        /// </summary>
        private const string CameraArbiterRequesterId = "PassthroughFrameSource";

        /// <summary>
        /// NetworkTables camera source for publishing stream info.
        /// </summary>
        private readonly INtCameraSource cameraSource;

        /// <summary>
        /// Configuration manager for settings.
        /// </summary>
        private readonly IConfigManager configManager;

        /// <summary>
        /// Main thread synchronization context for marshalling Unity API calls.
        /// </summary>
        private readonly SynchronizationContext mainThreadContext;

        /// <summary>
        /// Whether the frame source has been initialized.
        /// </summary>
        private bool isInitialized;

        /// <summary>
        /// Whether high quality streaming is enabled.
        /// </summary>
        private bool isHighQualityStreamEnabled;

        /// <summary>
        /// How the headset is rolled; frames are rotated upright before encoding.
        /// </summary>
        private CameraOrientation orientation;

        /// <summary>
        /// Cached base URL for stream endpoints.
        /// </summary>
        private string baseUrl;

        /// <summary>
        /// Whether frame capture is currently paused due to no active connections.
        /// </summary>
        private bool isPaused;

        /// <summary>
        /// JPEG compression quality (1-100). Higher values mean better quality and larger files.
        /// </summary>
        private int compressionQuality = 75;

        /// <summary>
        /// Total number of frame acquisition or JPEG encoding failures since startup.
        /// </summary>
        private int captureFailureCount;

        /// <summary>
        /// Number of consecutive capture failures since the last encoded frame.
        /// </summary>
        private int consecutiveCaptureFailures;

        /// <summary>
        /// UTC Unix timestamp, in milliseconds, of the last successfully encoded frame.
        /// Zero until the first frame is produced.
        /// </summary>
        private long lastSuccessfulFrameTimestampMs;

        /// <summary>
        /// UTC Unix timestamp, in milliseconds, of the last camera recovery request.
        /// </summary>
        private long lastCameraRecoveryTimestampMs;

        /// <summary>
        /// Protects the multi-field <see cref="EncodedFrame"/> value shared between the
        /// Unity capture thread and HTTP worker threads.
        /// </summary>
        private readonly object frameLock = new object();

        private EncodedFrame currentFrame;

        /// <summary>
        /// Function that returns whether frame capture should be paused.
        /// Set by VideoStreamProvider to control capture based on its state.
        /// </summary>
        public Func<bool> ShouldBePaused { get; set; } = () => true;

        /// <summary>
        /// Maximum desired framerate for capture/stream pacing
        /// </summary>
        public int MaxFrameRate => Math.Max(1, cameraSource?.Mode.Fps ?? 15);

        /// <summary>
        /// Gets whether the frame source is currently available for streaming.
        /// </summary>
        public bool IsAvailable => isInitialized;

        /// <summary>
        /// Gets the total number of recoverable capture/encode failures.
        /// </summary>
        public int CaptureFailureCount => Volatile.Read(ref captureFailureCount);

        /// <summary>
        /// Gets the number of failures since the last successfully encoded frame.
        /// </summary>
        public int ConsecutiveCaptureFailures => Volatile.Read(ref consecutiveCaptureFailures);

        /// <summary>
        /// Gets the UTC Unix timestamp, in milliseconds, of the last encoded frame.
        /// </summary>
        public long LastSuccessfulFrameTimestampMs =>
            Interlocked.Read(ref lastSuccessfulFrameTimestampMs);

        /// <summary>
        /// The current frame
        /// </summary>
        public EncodedFrame CurrentFrame
        {
            get
            {
                lock (frameLock)
                {
                    return currentFrame;
                }
            }
            private set
            {
                lock (frameLock)
                {
                    currentFrame = value;
                }
            }
        }

        /// <summary>
        /// The base URL for the web server.
        /// </summary>
        public string BaseUrl
        {
            get => baseUrl;
            set
            {
                if (string.Equals(baseUrl, value))
                {
                    return;
                }

                baseUrl = value;
                cameraSource.Streams = string.IsNullOrEmpty(baseUrl)
                    ? Array.Empty<string>()
                    : new[] { $"mjpg:{BaseUrl}/video" };
            }
        }

        /// <summary>
        /// Reference to the running frame capture coroutine.
        /// </summary>
        private Coroutine frameCaptureCoroutine;

        /// <summary>
        /// Changes the video mode and compression quality based on requested parameters. The best matching mode that
        /// meets or exceeds the requested parameters will be selected.
        /// </summary>
        /// <param name="width">Requested width, or null if not specified</param>
        /// <param name="height">Requested height, or null if not specified</param>
        /// <param name="fps">Requested FPS, or null if not specified</param>
        /// <param name="compression">Requested compression quality (1-100), or null if not specified</param>
        public async Task SetModeAndCompression(int? width, int? height, int? fps, int? compression)
        {
            // Save the config, which will trigger the change event handler to apply the changes to the stream
            if (width.HasValue || height.HasValue || fps.HasValue || compression.HasValue)
            {
                // Use the compression quality if it was specified, otherwise use the current quality
                var quality = compression.HasValue
                    ? Math.Clamp(compression.Value, 1, 100)
                    : compressionQuality;

                // Find the best matching mode
                var bestMatch = FindBestMatchingMode(width, height, fps);
                if (bestMatch.HasValue)
                {
                    QueuedLogger.Log(
                        $"Setting mode config to {bestMatch.Value} with quality {quality}"
                    );
                    await configManager.SetPassthroughStreamModeAsync(
                        new StreamMode(
                            bestMatch.Value.Width,
                            bestMatch.Value.Height,
                            bestMatch.Value.Fps,
                            quality
                        )
                    );
                }
                else
                {
                    // There was no suitable mode found, keep the current mode
                    var currentMode = cameraSource.Mode;
                    await configManager.SetPassthroughStreamModeAsync(
                        new StreamMode(
                            currentMode.Width,
                            currentMode.Height,
                            currentMode.Fps,
                            quality
                        )
                    );
                }
            }
        }

        /// <summary>
        /// Applies video mode and compression quality changes but does not persist to config.
        /// </summary>
        /// <param name="width">Requested width, or null if not specified</param>
        /// <param name="height">Requested height, or null if not specified</param>
        /// <param name="fps">Requested FPS, or null if not specified</param>
        /// <param name="compression">Requested compression quality (1-100), or null if not specified</param>
        private void ApplyModeAndCompression(int? width, int? height, int? fps, int? compression)
        {
            // Apply compression if specified
            if (compression.HasValue && compressionQuality != compression.Value)
            {
                compressionQuality = Math.Clamp(compression.Value, 1, 100);
                QueuedLogger.Log($"Switching compression quality: {compressionQuality}");
            }

            if (cameraSource?.Modes == null || cameraSource.Modes.Length == 0)
            {
                QueuedLogger.LogWarning("No video modes available");
                return;
            }

            // If no mode parameters specified, keep current mode
            if (width.HasValue || height.HasValue || fps.HasValue)
            {
                // Find the best matching mode
                VideoMode? bestMatch = FindBestMatchingMode(width, height, fps);
                if (bestMatch.HasValue && !cameraSource.Mode.Equals(bestMatch.Value))
                {
                    QueuedLogger.Log($"Switching to mode: {bestMatch.Value}");
                    cameraSource.Mode = bestMatch.Value;
                }
            }
        }

        /// <summary>
        /// Gets the available video modes.
        /// </summary>
        /// <returns>Array of available video modes.</returns>
        public VideoMode[] GetAvailableModes()
        {
            return cameraSource?.Modes ?? Array.Empty<VideoMode>();
        }

        /// <summary>
        /// Finds the best matching video mode that is closest to the requested parameters, without exceeding them.
        /// </summary>
        /// <param name="reqWidth">Requested width, or null to use the current width</param>
        /// <param name="reqheight">Requested height, or null to use the current height</param>
        /// <param name="reqFps">Requested FPS, or null to use the current FPS</param>
        /// <returns>Best matching mode, or null if no suitable mode found</returns>
        private VideoMode? FindBestMatchingMode(int? reqWidth, int? reqheight, int? reqFps)
        {
            // If no parameters are specified, there's nothing to match against.
            if (!reqWidth.HasValue && !reqheight.HasValue && !reqFps.HasValue)
            {
                return null;
            }

            // If any parameter is not specified, use the current mode's value for matching.
            var currentMode = cameraSource.Mode;
            var width = reqWidth ?? currentMode.Width;
            var height = reqheight ?? currentMode.Height;
            var fps = reqFps ?? currentMode.Fps;

            VideoMode? exactMatch = null;
            VideoMode? bestMatch = null;
            long bestScore = long.MaxValue;

            foreach (var mode in cameraSource.Modes)
            {
                // Restrict pixel count and fps unless high quality is enabled
                if (
                    !isHighQualityStreamEnabled
                    && (
                        (mode.Width * mode.Height) > VideoStream.MAX_LOW_QUAL_STREAM_PIXEL_COUNT
                        || mode.Fps > VideoStream.MAX_LOW_QUAL_FRAMERATE
                    )
                )
                {
                    continue;
                }

                // If a parameter is exceeded, skip this mode.
                if (mode.Width > width || mode.Height > height || mode.Fps > fps)
                {
                    continue;
                }

                // Check for exact match
                if (mode.Width == width && mode.Height == height && mode.Fps == fps)
                {
                    exactMatch = mode;
                    break;
                }

                // Calculate how close this mode is to the requested parameters
                // Lower is better (closer to what was requested)
                int pixelCountDiff = (width * height) - (mode.Width * mode.Height);
                int fpsDiff = fps - mode.Fps;

                // Total score (pixel count difference + weighted (100X) FPS difference)
                int totalDiff = pixelCountDiff + (fpsDiff * 100);
                if (totalDiff < bestScore)
                {
                    bestScore = totalDiff;
                    bestMatch = mode;
                }
            }
            QueuedLogger.Log($"Best match result: Exact={exactMatch}, Best={bestMatch}");
            return exactMatch ?? bestMatch;
        }

        /// <summary>
        /// Creates a new PassthroughFrameSource.
        /// </summary>
        /// <param name="coroutineHost">MonoBehaviour for coroutine execution</param>
        /// <param name="cameraAccess">Provides access to the PassthroughCamera through Meta's SDK</param>
        /// <param name="cameraArbiter">Single owner of camera enable/resolution. PFS reserves at low priority.</param>
        /// <param name="cameraSource">The network table source that will expose this camera stream</param>
        /// <param name="configManager">The config manager to update/querry config values</param>
        public PassthroughFrameSource(
            MonoBehaviour coroutineHost,
            PassthroughCameraAccess cameraAccess,
            CameraResourceManager cameraArbiter,
            INtCameraSource cameraSource,
            IConfigManager configManager
        )
        {
            this.coroutineHost = coroutineHost;
            this.cameraAccess = cameraAccess;
            this.cameraArbiter = cameraArbiter;
            this.cameraSource = cameraSource;
            this.configManager = configManager;

            // Capture main thread context for marshalling Unity API calls
            mainThreadContext = SynchronizationContext.Current;

            // Attach to ConfigManager callbacks
            configManager.OnEnablePassthroughStreamChanged += OnEnablePassthroughStreamChanged;
            configManager.OnPassthroughStreamModeChanged += OnPassthroughStreamModeChanged;
            configManager.OnEnableHighQualityStreamsChanged +=
                OnEnableHighQualityPassthroughStreamChanged;
            configManager.OnCameraOrientationChanged += OnCameraOrientationChanged;
        }

        /// <summary>
        /// Handles camera orientation config changes. Takes effect on the next frame.
        /// </summary>
        /// <param name="value">How the headset is rolled on its mount.</param>
        private void OnCameraOrientationChanged(CameraOrientation value)
        {
            orientation = value;
        }

        /// <summary>
        /// Handles video mode changes by requesting the new resolution from the
        /// camera arbiter. The arbiter may decline (return a different resolution)
        /// if a higher-priority requester (AprilTag detector) has the camera locked.
        /// </summary>
        /// <param name="mode">The new video mode.</param>
        private void OnSelectedModeChanged(VideoMode mode)
        {
            QueuedLogger.Log($"Changed mode: {mode}");
            var requested = new Vector2Int(mode.Width, mode.Height);
            var applied = cameraArbiter.Reserve(
                CameraArbiterRequesterId,
                requested,
                CameraRequestPriority.Low
            );
            if (applied != requested)
            {
                QueuedLogger.Log(
                    $"PassthroughFrameSource requested {requested.x}x{requested.y} "
                        + $"but camera arbiter applied {applied.x}x{applied.y} "
                        + "(higher-priority requester active)"
                );
            }
        }

        /// <summary>
        /// Handles stream mode configuration changes.
        /// </summary>
        /// <param name="streamMode">The new stream mode configuration.</param>
        private void OnPassthroughStreamModeChanged(StreamMode streamMode)
        {
            if (!isInitialized)
            {
                return;
            }

            QueuedLogger.Log($"Stream mode changed to {streamMode}");

            InvokeOnMainThread(() =>
            {
                // Apply without persisting (already persisted by the config manager)
                ApplyModeAndCompression(
                    streamMode.Width,
                    streamMode.Height,
                    streamMode.Framerate,
                    streamMode.Quality
                );

                // Check if the applied mode matches the requested mode. This handles case where the config value is
                // not valid. For example, high quality streaming could be disabled and the mode exceeded the allowed
                // resolution or the stored value could be invalid after a system or app update.
                var currentMode = cameraSource.Mode;
                if (
                    streamMode.Width != currentMode.Width
                    || streamMode.Height != currentMode.Height
                    || streamMode.Framerate != currentMode.Fps
                )
                {
                    // The applied mode is different, the config value was not valid. Persist the actual applied mode.
                    var appliedStreamMode = new StreamMode(
                        currentMode.Width,
                        currentMode.Height,
                        currentMode.Fps,
                        compressionQuality
                    );
                    configManager.SetPassthroughStreamModeAsync(appliedStreamMode);
                }
            });
        }

        /// <summary>
        /// Handles high quality stream enable/disable config changes.
        /// </summary>
        /// <param name="enabled">Whether high quality streaming should be enabled.</param>
        private async void OnEnableHighQualityPassthroughStreamChanged(bool enabled)
        {
            // Stream quality might need to be downgraded if high quality was just disabled
            bool checkForDowngrade = isHighQualityStreamEnabled && !enabled;
            isHighQualityStreamEnabled = enabled;

            // If high quality is disabled, check if we need to downgrade the resolution
            if (checkForDowngrade)
            {
                var currentMode = cameraSource.Mode;
                if (
                    currentMode.Width * currentMode.Height
                        > VideoStream.MAX_LOW_QUAL_STREAM_PIXEL_COUNT
                    || currentMode.Fps > VideoStream.MAX_LOW_QUAL_FRAMERATE
                )
                {
                    QueuedLogger.Log("High quality stream disabled, downgrading stream mode");
                    // Downgrade to best matching low quality mode
                    await SetModeAndCompression(
                        currentMode.Width,
                        currentMode.Height,
                        currentMode.Fps,
                        compressionQuality
                    );
                }
            }
        }

        /// <summary>
        /// Handles passthrough stream enable/disable config changes.
        /// </summary>
        /// <param name="enabled">Whether streaming should be enabled.</param>
        private async void OnEnablePassthroughStreamChanged(bool enabled)
        {
            if (cameraAccess is null)
            {
                QueuedLogger.Log("Disabled - cameraAccess is unset");
                return;
            }

            switch (enabled)
            {
                // Setting to enabled when already running
                case true when isInitialized:
                {
                    QueuedLogger.Log("Already initialized, skipping");
                    break;
                }
                // Setting to disabled when already not running
                case false when !isInitialized:
                {
                    QueuedLogger.Log("Already disabled, skipping");
                    break;
                }
                // Setting to enabled when not running
                case true when !isInitialized:
                {
                    if (cameraSource is null)
                    {
                        QueuedLogger.LogError(
                            "CameraSource was null! Cannot initialize passthrough"
                        );
                        break;
                    }

                    QueuedLogger.Log("Initializing passthrough camera...");

                    cameraSource.Description = "Quest Headset Camera";

                    // Populate the list of modes from the supported resolutions
                    var supportedResolutions = PassthroughCameraAccess.GetSupportedResolutions(
                        cameraAccess.CameraPosition
                    );
                    var modes = new VideoMode[supportedResolutions.Length * FpsOptions.Length];
                    int i = 0;
                    foreach (var resolution in supportedResolutions)
                    {
                        foreach (var fps in FpsOptions)
                        {
                            modes[i++] = new VideoMode(
                                PixelFormat.MJPEG,
                                resolution.x,
                                resolution.y,
                                fps
                            );
                        }
                    }

                    cameraSource.Modes = modes;
                    cameraSource.SelectedModeChanged += OnSelectedModeChanged;

                    // Load video mode and quality from config
                    var streamMode = await configManager.GetPassthroughStreamModeAsync();
                    compressionQuality = streamMode.Quality;

                    // Find best matching mode for the configured stream mode
                    var bestMatch = FindBestMatchingMode(
                        streamMode.Width,
                        streamMode.Height,
                        streamMode.Framerate
                    );

                    if (bestMatch.HasValue)
                    {
                        cameraSource.Mode = bestMatch.Value;
                        QueuedLogger.Log(
                            $"Selected mode {bestMatch.Value} with quality {compressionQuality}"
                        );
                    }
                    else
                    {
                        // Fallback to default mode if no match found
                        cameraSource.Mode = cameraSource.Modes[3];
                        QueuedLogger.LogWarning(
                            $"Could not find matching mode for {streamMode}, using {cameraSource.Mode}"
                        );
                    }

                    // Start initialization coroutine
                    frameCaptureCoroutine = coroutineHost.StartCoroutine(FrameCaptureCoroutine());
                    isInitialized = true;
                    cameraSource.IsConnected = true;

                    break;
                }
                // Setting to disabled when running
                case false when isInitialized:
                {
                    QueuedLogger.Log("Disabling Passthrough");

                    // Remove callback from cameraSource so a stale mode change can't re-reserve
                    cameraSource.SelectedModeChanged -= OnSelectedModeChanged;

                    // Stop Coroutine
                    if (frameCaptureCoroutine != null)
                    {
                        coroutineHost.StopCoroutine(frameCaptureCoroutine);
                        frameCaptureCoroutine = null;
                    }

                    // Drop any frame still being read back or encoded, and free the encoder.
                    captureGeneration++;
                    StopEncoder(EncoderStopTimeout);

                    // Release our camera reservation. If the AprilTag detector is also
                    // active and holding a high-priority reservation, the camera stays on
                    // at the AprilTag-pinned resolution; otherwise it shuts down.
                    cameraArbiter.Release(CameraArbiterRequesterId);

                    isInitialized = false;
                    cameraSource.IsConnected = false;
                    break;
                }
            }
        }

        /// <summary>
        /// Captures frames from the passthrough camera at the requested frame rate and encodes
        /// them as JPEG. The main thread only starts a non-blocking GPU readback of each frame;
        /// <see cref="encoder"/> compresses it on its own thread.
        /// </summary>
        public IEnumerator FrameCaptureCoroutine()
        {
            QueuedLogger.Log("Initializing LJPGT");
            double nextFrameAtMs = 0;

            while (true)
            {
                // Check if the desired pause state has changed and sync it

                bool shouldBePaused = GetShouldBePaused();

                if (shouldBePaused && !isPaused)
                {
                    isPaused = true;
                    captureGeneration++;
                    CurrentFrame = default;
                    QueuedLogger.Log("Paused capture");
                }
                else if (!shouldBePaused && isPaused)
                {
                    isPaused = false;
                    nextFrameAtMs = 0;
                    Interlocked.Exchange(
                        ref lastCameraRecoveryTimestampMs,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    );
                    QueuedLogger.Log("Resumed capture");
                }

                // If paused, yield until unpaused
                if (isPaused)
                {
                    yield return null;
                    continue;
                }

                double nowMs = Time.realtimeSinceStartupAsDouble * 1000.0;
                double deadline = nextFrameAtMs;
                if (FrameCadence.TryTake(nowMs, 1000.0 / MaxFrameRate, ref deadline))
                {
                    CaptureAttempt result = TryCaptureFrame();
                    if (result != CaptureAttempt.NotReady)
                    {
                        // Preserve phase across the camera's frame grid. Only consume a
                        // deadline when capture starts (or fails); busy frames can retry.
                        nextFrameAtMs = deadline;
                    }
                }
                yield return null;
            }
        }

        /// <summary>
        /// Starts the readback of one frame. All SDK and readback failures are contained here
        /// so a transient fault cannot terminate <see cref="FrameCaptureCoroutine"/>.
        /// </summary>
        private CaptureAttempt TryCaptureFrame()
        {
            try
            {
                // An encoder that outlived its stop still owns the buffers and would discard
                // every frame. Replace it once it has finished.
                if (encoderStopPending && !StopEncoder(TimeSpan.Zero))
                {
                    return CaptureAttempt.NotReady;
                }

                // The camera arbiter briefly disables the camera while applying a new
                // resolution. Likewise, the SDK may need several frames before reporting
                // an update after it is re-enabled. Neither condition is a failure, and
                // neither is the previous frame still being read back or encoded.
                if (!cameraAccess.enabled || !cameraAccess.IsUpdatedThisFrame || captureBusy)
                {
                    TryRecoverStalledCamera();
                    return CaptureAttempt.NotReady;
                }

                Texture texture = cameraAccess.GetTexture();
                var frame = new StreamFrame
                {
                    Generation = captureGeneration,
                    FrameNumber = Time.frameCount,
                    Orientation = orientation,
                    Quality = compressionQuality,
                };
                captureBusy = true;
                if (
                    !streamReadback.TryRequest(
                        texture,
                        succeeded => OnStreamReadbackCompleted(frame, succeeded),
                        out string failure
                    )
                )
                {
                    captureBusy = false;
                    RecordCaptureFailure(failure);
                    TryRecoverStalledCamera();
                    return CaptureAttempt.Failed;
                }
                return CaptureAttempt.Succeeded;
            }
            catch (Exception ex)
            {
                if (!streamReadback.InFlight)
                {
                    captureBusy = false;
                }
                string permissionHint =
                    ex is NullReferenceException
                        ? " Verify that the 'Headset Cameras' app permission is enabled."
                        : string.Empty;
                RecordCaptureFailure($"Frame capture failed.{permissionHint}", ex);
                TryRecoverStalledCamera();
                return CaptureAttempt.Failed;
            }
        }

        /// <summary>Main thread, when a frame's GPU copy finishes.</summary>
        private void OnStreamReadbackCompleted(StreamFrame frame, bool succeeded)
        {
            if (frame.Generation != captureGeneration)
            {
                captureBusy = false;
                return;
            }
            if (!succeeded)
            {
                captureBusy = false;
                RecordCaptureFailure("The GPU readback of the camera frame failed");
                return;
            }
            encoder ??= new LatestOnlyWorker<StreamFrame>(
                "Passthrough stream encoder",
                EncodeFrame,
                _ => captureBusy = false
            );
            encoder.Post(frame);
        }

        /// <summary>
        /// Encoder thread. Compresses the completed readback, then frees it for the next frame.
        /// </summary>
        private void EncodeFrame(StreamFrame frame)
        {
            try
            {
                if (frame.Generation != captureGeneration)
                {
                    return;
                }
                compressor ??= new LJTCompressor();
                byte[] rawJpeg =
                    frame.Orientation == CameraOrientation.NORMAL
                        ? compressor.Compress(
                            streamReadback.Data,
                            streamReadback.Width * 4,
                            streamReadback.Width,
                            streamReadback.Height,
                            LJTPixelFormat.RGBA,
                            LJTSubsamplingOption.Chrominance420,
                            frame.Quality,
                            LJTFlags.BottomUp
                        )
                        : EncodeRotatedJpeg(frame);
                if (rawJpeg == null || rawJpeg.Length == 0)
                {
                    RecordCaptureFailure("The JPEG compressor returned an empty frame");
                    DropCompressor();
                    return;
                }
                if (frame.Generation != captureGeneration)
                {
                    return;
                }

                CurrentFrame = new EncodedFrame(frame.FrameNumber, rawJpeg);
                Interlocked.Exchange(ref consecutiveCaptureFailures, 0);
                Interlocked.Exchange(
                    ref lastSuccessfulFrameTimestampMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                );
            }
            catch (Exception ex)
            {
                // Avoid reusing native encoder state after an encoding failure.
                DropCompressor();
                RecordCaptureFailure("JPEG encoding failed.", ex);
            }
            finally
            {
                captureBusy = false;
            }
        }

        private void DropCompressor()
        {
            compressor?.Dispose();
            compressor = null;
        }

        /// <summary>Stops streaming and frees the encoder. Call when the app shuts down.</summary>
        public void Shutdown()
        {
            if (frameCaptureCoroutine != null)
            {
                coroutineHost.StopCoroutine(frameCaptureCoroutine);
                frameCaptureCoroutine = null;
            }
            captureGeneration++;
            StopEncoder(EncoderStopTimeout);
        }

        /// <summary>
        /// Main thread. Stops the encoder thread after its current frame, then frees the readback
        /// buffer (waiting for a copy still in flight), the compressor and the scratch buffer. If
        /// the encoder does not stop in time they stay allocated and capture retries before its
        /// next frame (<see cref="encoderStopPending"/>). Returns whether the encoder is gone.
        /// </summary>
        private bool StopEncoder(TimeSpan timeout)
        {
            if (encoder != null && !encoder.Stop(timeout))
            {
                if (timeout > TimeSpan.Zero)
                {
                    QueuedLogger.LogError(
                        $"Passthrough stream encoder did not stop within {timeout.TotalSeconds:F0} s; "
                            + "leaving its buffers allocated until it finishes."
                    );
                }
                encoderStopPending = true;
                return false;
            }
            encoderStopPending = false;
            encoder = null;
            streamReadback.Dispose();
            DropCompressor();
            rotationBuffer = null;
            captureBusy = false;
            return true;
        }

        /// <summary>
        /// Periodically bounces the camera through the arbiter when an active stream has
        /// produced no frames. The arbiter always reapplies the highest-priority request,
        /// preserving AprilTag's resolution lock.
        /// </summary>
        private void TryRecoverStalledCamera()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long lastFrame = Interlocked.Read(ref lastSuccessfulFrameTimestampMs);
            long lastRecovery = Interlocked.Read(ref lastCameraRecoveryTimestampMs);
            long baseline = Math.Max(lastFrame, lastRecovery);
            if (baseline == 0)
            {
                Interlocked.CompareExchange(ref lastCameraRecoveryTimestampMs, now, 0);
                return;
            }
            if (now - baseline < CameraRecoveryIntervalMs)
            {
                return;
            }
            if (
                Interlocked.CompareExchange(ref lastCameraRecoveryTimestampMs, now, lastRecovery)
                != lastRecovery
            )
            {
                return;
            }

            QueuedLogger.LogWarning(
                "No passthrough frame has been encoded for 5 seconds; reapplying the active camera reservation"
            );
            cameraArbiter.ReapplyCurrentResolution();
        }

        /// <summary>
        /// Reads the provider's pause callback without allowing an unexpected callback
        /// exception to terminate frame capture.
        /// </summary>
        private bool GetShouldBePaused()
        {
            try
            {
                return ShouldBePaused?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                RecordCaptureFailure("Failed to query stream pause state", ex);
                return true;
            }
        }

        /// <summary>
        /// Records a recoverable failure, with rate-limited logging to avoid flooding the
        /// device log during an extended camera outage.
        /// </summary>
        private void RecordCaptureFailure(string message, Exception ex = null)
        {
            Interlocked.Increment(ref captureFailureCount);
            int consecutive = Interlocked.Increment(ref consecutiveCaptureFailures);
            if (consecutive == 1 || consecutive % 60 == 0)
            {
                string detail = ex == null ? string.Empty : $" {ex.GetType().Name}: {ex.Message}";
                QueuedLogger.LogError(
                    $"{message}{detail} (consecutive failures: {consecutive}; capture will retry)"
                );
            }
        }

        /// <summary>
        /// Encoder thread. Rotates the bottom-up readback upright for the frame's orientation
        /// and encodes it.
        /// </summary>
        private unsafe byte[] EncodeRotatedJpeg(StreamFrame frame)
        {
            const int bytesPerPixel = 4;
            int width = streamReadback.Width;
            int height = streamReadback.Height;
            int bytes = width * height * bytesPerPixel;
            if (rotationBuffer == null || rotationBuffer.Length != bytes)
            {
                rotationBuffer = new byte[bytes];
            }
            CameraOrientationMath.RotateSize(
                frame.Orientation,
                width,
                height,
                out int rotatedWidth,
                out int rotatedHeight
            );
            fixed (byte* rotated = rotationBuffer)
            {
                ImageRotation.Rotate(
                    (byte*)streamReadback.Data,
                    width,
                    height,
                    width * bytesPerPixel,
                    bytesPerPixel,
                    1,
                    frame.Orientation,
                    rotated
                );
                return compressor.Compress(
                    (IntPtr)rotated,
                    rotatedWidth * bytesPerPixel,
                    rotatedWidth,
                    rotatedHeight,
                    LJTPixelFormat.RGBA,
                    LJTSubsamplingOption.Chrominance420,
                    frame.Quality,
                    LJTFlags.None
                );
            }
        }

        private enum CaptureAttempt
        {
            NotReady,
            Succeeded,
            Failed,
        }

        /// <summary>What the encoder needs about a frame, captured when its readback started.</summary>
        private sealed class StreamFrame
        {
            public int Generation;
            public int FrameNumber;
            public CameraOrientation Orientation;
            public int Quality;
        }

        /// <summary>
        /// Non-blocking copy of the camera texture. Reused for every frame: the next readback
        /// starts only after the previous frame is encoded (<see cref="captureBusy"/>).
        /// </summary>
        private readonly CameraFrameReadback streamReadback = new CameraFrameReadback();

        /// <summary>Compresses frames off the main thread. Created with the first frame.</summary>
        private LatestOnlyWorker<StreamFrame> encoder;

        /// <summary>How long stopping the stream waits for the frame being encoded.</summary>
        private static readonly TimeSpan EncoderStopTimeout = TimeSpan.FromSeconds(2);

        /// <summary>A stop timed out; the stopped encoder must be cleaned up before reuse.</summary>
        private bool encoderStopPending;

        /// <summary>Encoder-owned JPEG compressor and rotation scratch buffer.</summary>
        private LJTCompressor compressor;
        private byte[] rotationBuffer;

        /// <summary>A frame is being read back or encoded, so the readback buffer is in use.</summary>
        private volatile bool captureBusy;

        /// <summary>
        /// Changes when capture pauses or stops, so a frame started before then is not published.
        /// </summary>
        private volatile int captureGeneration;

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
