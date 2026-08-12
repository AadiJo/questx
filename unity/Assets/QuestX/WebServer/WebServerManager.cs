using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        /// Gets the base URL of the server
        /// </summary>
        string BaseUrl { get; }

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
        private readonly IConfigManager configManager;
        private readonly LogCollector logCollector;
        private readonly CameraResourceManager cameraArbiter;

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

        public WebServerManager(
            IConfigManager configManager,
            INetworkTableConnection networkTableConnection,
            Transform vrCamera,
            Transform vrCameraRoot,
            VideoStreamProvider.IFrameSource frameSource,
            CameraResourceManager cameraArbiter,
            PassthroughCameraAccess cameraAccess,
            Transform resetTransform
        )
        {
            this.configManager = configManager;
            this.networkTableConnection = networkTableConnection;
            this.vrCamera = vrCamera;
            this.vrCameraRoot = vrCameraRoot;
            this.resetTransform = resetTransform;
            this.cameraArbiter = cameraArbiter;
            this.cameraAccess = cameraAccess;

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
        /// Gets the base public URL of the server
        /// Does not include a trailing slash
        /// </summary>
        public string BaseUrl { get; private set; }
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
            string ipAddress = GetLocalIPAddress();
            float currentFps = 1f / Time.deltaTime;

            statusProvider?.UpdateStatus(
                position,
                rotation,
                isTracking,
                trackingLostEvents,
                SystemInfo.batteryLevel,
                SystemInfo.batteryStatus,
                networkTableConnection.IsConnected,
                ipAddress,
                cachedTeamNumber,
                cachedRobotIpAddress,
                currentFps,
                Time.frameCount
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

            BaseUrl = $"http://{ipAddress}:{QuestXConstants.WebServer.SERVER_PORT}";
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
        /// Executes pose reset to origin (0,0,0) with no rotation.
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
                vrCamera,
                vrCameraRoot,
                resetTransform
            );

            // Execute the pose reset using the existing command implementation
            webPoseResetCommand.Execute(command);

            QueuedLogger.Log("[QuestX] Pose reset to origin completed");
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
                cameraAccess
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
            await FileManager.ExtractAndroidFileAsync("logo.svg", "ui", targetPath);
            await FileManager.ExtractAndroidFileAsync("logo-dark.svg", "ui", targetPath);

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
        private string GetLocalIPAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString() ?? "0.0.0.0";
                }
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        private void ShowConnectionInfo()
        {
            QueuedLogger.Log(
                $"Connect to WebUI at http://{GetLocalIPAddress()}:{QuestXConstants.WebServer.SERVER_PORT}"
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
