using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using QuestX.Camera;
using QuestX.Commands;
using QuestX.Commands.Commands;
using QuestX.Config;
using QuestX.Core;
using QuestX.Network;
using QuestX.Protos.Generated;
using QuestX.QuestX.AprilTag;
using QuestX.Utils;
using QuestX.WebServer.Server;
using UnityEngine;
using Wpi.Proto;

namespace QuestX.WebServer
{
    /// <summary>
    /// Interface for WebServer management.
    /// </summary>
    public interface IWebServerManager
    {
        /// <summary>
        /// Gets whether the HTTP server is currently running.
        /// </summary>
        bool IsServerRunning { get; }

        /// <summary>
        /// Gets the base URL of the server, or null when the headset has no address
        /// </summary>
        string BaseUrl { get; }

        /// <summary>
        /// Gets the headset's local IPv4 address, or null when it has none
        /// </summary>
        string IpAddress { get; }

        /// <summary>
        /// Initializes the web server asynchronously.
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Periodic update method. Called from QuestX.SlowUpdate() at 3Hz.
        /// </summary>
        void Periodic(
            Vector3 position,
            Quaternion rotation,
            bool isTracking,
            int trackingLostEvents,
            uint trackingEpoch,
            AprilTagDiagnosticsSnapshot aprilTagDiagnostics
        );

        /// <summary>
        /// Stops the web server and cleans up resources.
        /// </summary>
        void Shutdown();
    }

    /// <summary>
    /// Manages the QuestX configuration web server.
    /// Provides HTTP endpoints for configuration, status, and control.
    /// </summary>
    public class WebServerManager : IWebServerManager
    {
        private readonly VideoStreamProvider streamProvider;
        private readonly PassthroughFrameSource passthroughFrameSource;
        private readonly StatusProvider statusProvider;
        private ConfigServer server;

        private SynchronizationContext mainThreadContext;
        private readonly Transform resetTransform;
        private readonly Transform vrCameraRoot;
        private readonly INetworkTableConnection networkTableConnection;
        private readonly Transform vrCamera;
        private readonly VioReferenceFrame vioFrame;
        private readonly IConfigManager configManager;
        private readonly LogCollector logCollector;
        private readonly CameraResourceManager cameraArbiter;
        private readonly AprilTagOverlay aprilTagOverlay;
        private readonly SceneProvider sceneProvider;
        private readonly RobotMount robotMount;

        /// <summary>
        /// Meta SDK camera reference. Forwarded to <see cref="ConfigServer"/> so the
        /// AprilTag video-modes endpoint can enumerate supported resolutions even when
        /// the passthrough stream is not currently running.
        /// </summary>
        private readonly PassthroughCameraAccess cameraAccess;

        private bool isInitialized;

        // Cached values updated via config events
        private int cachedTeamNumber;
        private string cachedDebugIpOverride = "";
        private string cachedRobotIpAddress = "";

        // App FPS is averaged over the frames since the previous status update rather than
        // taken from a single frame's deltaTime, which jumps with every slow frame.
        private int fpsWindowFrameCount = -1;
        private double fpsWindowStart;

        public WebServerManager(
            IConfigManager configManager,
            INetworkTableConnection networkTableConnection,
            VioReferenceFrame vioFrame,
            Transform vrCamera,
            Transform vrCameraRoot,
            VideoStreamProvider.IFrameSource frameSource,
            CameraResourceManager cameraArbiter,
            PassthroughCameraAccess cameraAccess,
            Transform resetTransform,
            AprilTagOverlay aprilTagOverlay,
            SceneProvider sceneProvider,
            RobotMount robotMount
        )
        {
            this.configManager = configManager;
            this.networkTableConnection = networkTableConnection;
            this.vioFrame = vioFrame;
            this.vrCamera = vrCamera;
            this.vrCameraRoot = vrCameraRoot;
            this.resetTransform = resetTransform;
            this.cameraArbiter = cameraArbiter;
            this.cameraAccess = cameraAccess;
            this.aprilTagOverlay = aprilTagOverlay;
            this.sceneProvider = sceneProvider;
            this.robotMount = robotMount;

            statusProvider = new StatusProvider();
            logCollector = new LogCollector();
            streamProvider = new VideoStreamProvider(frameSource);
            passthroughFrameSource = frameSource as PassthroughFrameSource;

            // Subscribe to config change events
            configManager.OnTeamNumberChanged += OnTeamNumberChanged;
            configManager.OnDebugIpOverrideChanged += OnDebugIpOverrideChanged;

            // Forward camera arbitration changes into the status provider so the web UI
            // can show "Locked by AprilTag" and the actual effective resolution.
            cameraArbiter.OnResolutionChanged += OnEffectiveCameraResolutionChanged;
        }

        /// <summary>
        /// Subscriber for <see cref="CameraResourceManager.OnResolutionChanged"/>. Pushes the
        /// new effective resolution and lock state into the status provider for the web UI.
        /// </summary>
        private void OnEffectiveCameraResolutionChanged(Vector2Int? resolution)
        {
            statusProvider?.UpdateCameraStatus(cameraArbiter.IsLockedByHighPriority, resolution);
        }

        #region Properties
        public bool IsServerRunning => server?.IsRunning ?? false;

        /// <summary>
        /// Gets the base public URL of the server, or null when the headset has no address
        /// Does not include a trailing slash
        /// </summary>
        public string BaseUrl { get; private set; }

        /// <summary>
        /// Gets the headset's local IPv4 address, or null when it has none
        /// </summary>
        public string IpAddress { get; private set; }
        #endregion

        #region Event Subscribers
        private void OnTeamNumberChanged(int teamNumber)
        {
            cachedTeamNumber = teamNumber;
            UpdateRobotIpAddress();
        }

        private void OnDebugIpOverrideChanged(string debugIpOverride)
        {
            cachedDebugIpOverride = debugIpOverride ?? "";
            UpdateRobotIpAddress();
        }

        private void UpdateRobotIpAddress()
        {
            if (!string.IsNullOrEmpty(cachedDebugIpOverride))
            {
                cachedRobotIpAddress = cachedDebugIpOverride;
            }
            else if (cachedTeamNumber > 0)
            {
                cachedRobotIpAddress = $"10.{cachedTeamNumber / 100}.{cachedTeamNumber % 100}.2";
            }
            else
            {
                cachedRobotIpAddress = "";
            }
        }
        #endregion

        #region Lifecycle Methods
        public async Task InitializeAsync()
        {
            mainThreadContext = SynchronizationContext.Current;

            if (isInitialized)
            {
                QueuedLogger.Log("Already initialized, skipping");
                return;
            }

            QueuedLogger.Log("Initializing...");

            logCollector.Initialize();

            await StartServerAsync();

            isInitialized = true;
            QueuedLogger.Log("Initialization complete");
        }

        public void Shutdown()
        {
            QueuedLogger.Log("Shutting down...");

            configManager.OnTeamNumberChanged -= OnTeamNumberChanged;
            configManager.OnDebugIpOverrideChanged -= OnDebugIpOverrideChanged;

            if (cameraArbiter != null)
            {
                cameraArbiter.OnResolutionChanged -= OnEffectiveCameraResolutionChanged;
            }

            server?.Stop();
            server = null;
            logCollector?.Dispose();

            QueuedLogger.Log("Shutdown complete");
        }
        #endregion

        #region Periodic
        public void Periodic(
            Vector3 position,
            Quaternion rotation,
            bool isTracking,
            int trackingLostEvents,
            uint trackingEpoch,
            AprilTagDiagnosticsSnapshot aprilTagDiagnostics
        )
        {
            // Shared with NetworkTableConnection, which looks it up off the main thread
            string ipAddress = networkTableConnection.IpAddress;
            if (ipAddress == LocalAddressMonitor.NoAddress)
                ipAddress = null;

            double now = Time.realtimeSinceStartupAsDouble;

            int frameCount = Time.frameCount;
            float currentFps =
                fpsWindowFrameCount >= 0 && now > fpsWindowStart
                    ? (float)((frameCount - fpsWindowFrameCount) / (now - fpsWindowStart))
                    : 1f / Time.deltaTime;
            fpsWindowFrameCount = frameCount;
            fpsWindowStart = now;

            statusProvider?.UpdateStatus(
                position,
                rotation,
                isTracking,
                trackingLostEvents,
                SystemInfo.batteryLevel,
                SystemInfo.batteryStatus,
                networkTableConnection.IsConnected,
                ipAddress ?? "0.0.0.0",
                cachedTeamNumber,
                cachedRobotIpAddress,
                currentFps,
                frameCount
            );
            statusProvider?.UpdateMeasurementStatus(
                trackingEpoch,
                Time.timeAsDouble,
                networkTableConnection.VioPublishCount,
                networkTableConnection.AprilTagPublishCount,
                aprilTagDiagnostics
            );
            if (passthroughFrameSource != null)
            {
                statusProvider?.UpdatePassthroughStreamStatus(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    passthroughFrameSource.LastSuccessfulFrameTimestampMs,
                    passthroughFrameSource.CaptureFailureCount,
                    passthroughFrameSource.ConsecutiveCaptureFailures,
                    streamProvider.ConnectedClientCount
                );
            }

            if (ipAddress != IpAddress)
            {
                IpAddress = ipAddress;
                BaseUrl =
                    ipAddress == null
                        ? null
                        : $"http://{ipAddress}:{QuestXConstants.WebServer.SERVER_PORT}";
                ShowConnectionInfo();
            }
        }
        #endregion

        #region Main Thread Callbacks
        /// <summary>
        /// Requests a pose reset to be executed on the main thread.
        /// Called from ConfigServer on background HTTP thread.
        /// </summary>
        internal void RequestPoseReset()
        {
            QueuedLogger.Log("Pose reset requested from web interface");
            mainThreadContext.Post(_ => ExecutePoseResetToOrigin(), null);
        }

        /// <summary>
        /// Executes a pose reset that puts the robot at the origin (0,0,0) with no rotation.
        /// Uses the existing PoseResetCommand implementation to ensure single source of truth.
        /// </summary>
        private void ExecutePoseResetToOrigin()
        {
            QueuedLogger.Log("Web interface requested pose reset to origin");

            // Create a protobuf command payload for origin reset in FRC coordinates
            var resetPose = new ProtobufPose3d
            {
                Translation = new ProtobufTranslation3d
                {
                    X = 0,
                    Y = 0,
                    Z = 0,
                },
                Rotation = new ProtobufRotation3d
                {
                    Q = new ProtobufQuaternion
                    {
                        X = 0,
                        Y = 0,
                        Z = 0,
                        W = 1,
                    },
                },
            };

            var command = new ProtobufQuestXCommand
            {
                Type = QuestXCommandType.PoseReset,
                CommandId = (uint)DateTime.UtcNow.Ticks,
                PoseResetPayload = new ProtobufQuestXPoseResetPayload { TargetPose = resetPose },
            };

            // Create web command context for web-initiated reset
            // (no NetworkTables response needed for web interface)
            var webContext = new WebCommandContext();

            // Create a temporary command instance for web-initiated reset
            var webPoseResetCommand = new PoseResetCommand(
                webContext, // Web context is no-op (no NetworkTables responses)
                vioFrame,
                robotMount,
                vrCamera,
                vrCameraRoot,
                resetTransform
            );

            // Execute the pose reset using the existing command implementation
            webPoseResetCommand.Execute(command);

            QueuedLogger.Log("[QuestX] Pose reset to origin processed");
        }

        /// <summary>
        /// Requests an app restart to be executed on the main thread.
        /// Called from ConfigServer on background HTTP thread.
        /// </summary>
        internal void RequestRestart()
        {
            QueuedLogger.Log("Restart requested from web interface");
            mainThreadContext.Post(_ => RestartApp(), null);
        }
        #endregion

        #region Server Setup
        private async Task StartServerAsync()
        {
            QueuedLogger.Log("Starting configuration server...");

            string staticPath = FileManager.GetStaticFilesPath("ui");
            if (string.IsNullOrEmpty(staticPath))
            {
                QueuedLogger.LogError("Failed to get static files path");
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            await ExtractAndroidUIFilesAsync(staticPath);
#else
            EnsureStaticFilesExist(staticPath);
            await Task.CompletedTask;
#endif

            server = new ConfigServer(
                configManager,
                QuestXConstants.WebServer.SERVER_PORT,
                QuestXConstants.WebServer.ENABLE_CORS_DEV_MODE,
                staticPath,
                this,
                statusProvider,
                logCollector,
                streamProvider,
                cameraAccess,
                aprilTagOverlay,
                sceneProvider
            );

            await server.StartAsync();

            if (!server.IsRunning)
            {
                QueuedLogger.LogError("Server did not start successfully");
                return;
            }

            ShowConnectionInfo();
            QueuedLogger.Log("Server started successfully");
        }
        #endregion

        #region Static File Management
#if UNITY_ANDROID && !UNITY_EDITOR
        private async Task ExtractAndroidUIFilesAsync(string targetPath)
        {
            if (Directory.Exists(targetPath))
            {
                QueuedLogger.Log("Clearing old UI files...");
                Directory.Delete(targetPath, true);
            }

            QueuedLogger.Log("Extracting UI files from APK...");

            Directory.CreateDirectory(targetPath);
            string assetsDir = Path.Combine(targetPath, "assets");
            Directory.CreateDirectory(assetsDir);

            await FileManager.ExtractAndroidFileAsync("index.html", "ui", targetPath);
            await FileManager.ExtractAndroidFileAsync("main.css", "ui/assets", assetsDir);
            await FileManager.ExtractAndroidFileAsync("main.js", "ui/assets", assetsDir);
            await FileManager.ExtractAndroidFileAsync(
                "geist-latin-wght-normal.woff2",
                "ui/assets",
                assetsDir
            );
            await FileManager.ExtractAndroidFileAsync("bird-logo.svg", "ui", targetPath);

            QueuedLogger.Log("UI extraction complete");
        }
#endif

        private void EnsureStaticFilesExist(string path)
        {
            if (Directory.Exists(path))
                return;

            Directory.CreateDirectory(path);

            string fallbackSourcePath = Path.Combine(
                Application.streamingAssetsPath,
                "ui",
                "fallback.html"
            );
            string fallbackTargetPath = Path.Combine(path, "index.html");

            try
            {
                if (File.Exists(fallbackSourcePath))
                {
                    File.Copy(fallbackSourcePath, fallbackTargetPath);
                    QueuedLogger.Log($"Copied fallback HTML from {fallbackSourcePath}");
                }
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError($"Failed to copy fallback HTML: {ex.Message}");
            }
        }
        #endregion

        #region Utility Methods
        private void ShowConnectionInfo()
        {
            QueuedLogger.Log(
                BaseUrl == null
                    ? $"WebUI is waiting for a network address (port {QuestXConstants.WebServer.SERVER_PORT})"
                    : $"Connect to WebUI at {BaseUrl}"
            );
        }

        private void RestartApp()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var pm = activity.Call<AndroidJavaObject>("getPackageManager"))
                using (
                    var intent = pm.Call<AndroidJavaObject>(
                        "getLaunchIntentForPackage",
                        Application.identifier
                    )
                )
                {
                    const int FLAG_ACTIVITY_NEW_TASK = 0x10000000;
                    const int FLAG_ACTIVITY_CLEAR_TASK = 0x00008000;
                    const int FLAG_ACTIVITY_CLEAR_TOP = 0x04000000;

                    intent.Call<AndroidJavaObject>(
                        "addFlags",
                        FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK | FLAG_ACTIVITY_CLEAR_TOP
                    );
                    activity.Call("startActivity", intent);

                    QueuedLogger.Log("New instance started, killing current process...");
                }

                using (var process = new AndroidJavaClass("android.os.Process"))
                {
                    int pid = process.CallStatic<int>("myPid");
                    process.CallStatic("killProcess", pid);
                }
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError($"Failed to restart: {ex.Message}");
                Application.Quit();
            }
#else
            Application.Quit();
#endif
        }
        #endregion
    }
}
