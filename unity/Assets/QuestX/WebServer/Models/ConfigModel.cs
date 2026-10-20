using System;
using System.Collections.Generic;

namespace QuestX.WebServer
{
    /// <summary>
    /// Stream mode configuration for web API
    /// </summary>
    [Serializable]
    public class StreamModeModel
    {
        public int width;
        public int height;
        public int framerate;
        public int quality;
    }

    /// <summary>
    /// Headset center-eye pose in the robot frame: meters and degrees, WPILib axes.
    /// </summary>
    [Serializable]
    public class RobotToQuestModel
    {
        public double x;
        public double y;
        public double z;
        public double roll;
        public double pitch;
        public double yaw;
    }

    /// <summary>
    /// Available video mode option for the current stream source
    /// </summary>
    [Serializable]
    public class VideoModeModel
    {
        public int width;
        public int height;
        public int framerate;
    }

    /// <summary>
    /// AprilTag detector configuration for web API.
    /// <c>ignoredIds</c> is a blacklist: detections with one of these IDs are dropped
    /// before the PoseLib solver runs. Empty array means detect every tag.
    /// <c>fieldLayoutFile</c> takes effect on the next app restart only.
    /// </summary>
    [Serializable]
    public class AprilTagDetectorModeModel
    {
        public int mode;
        public int width;
        public int height;
        public int framerate;
        public int[] ignoredIds;
        public double maxDistance;
        public int minimumNumberOfTags;

        /// <summary>
        /// Filename of the field-layout JSON to use on the next app restart. Resolved
        /// against the bundled directory and the user-uploaded directory.
        /// Restart-on-change; the running app keeps using the previously-loaded layout
        /// until restart.
        /// </summary>
        public string fieldLayoutFile;

        /// <summary>
        /// Legacy Quest-fusion value retained for configuration downgrade compatibility.
        /// The current app does not use it when producing AprilTag measurements.
        /// </summary>
        public int? confidencePreset;

        /// <summary>
        /// Legacy Quest-fusion noise scale retained for configuration downgrade compatibility.
        /// The current app reports per-measurement uncertainty instead.
        /// </summary>
        public double? noiseScale;

        /// <summary>
        /// Tag detector backend: 0 = Napa (default), 1 = libapriltag. Omit to keep the
        /// current backend. Switching takes effect on the next camera frame.
        /// </summary>
        public int? detector;
    }

    /// <summary>
    /// One entry in the response from <c>GET /api/apriltag-field-layouts</c>.
    /// </summary>
    [Serializable]
    public class AprilTagFieldLayoutEntry
    {
        /// <summary>The literal filename (e.g. "2026-rebuilt-welded.json").</summary>
        public string fileName;

        /// <summary>Human-friendly name derived from the filename for display in the UI.</summary>
        public string displayName;

        /// <summary>"bundled" or "custom" (user-uploaded via the manage modal).</summary>
        public string source;

        /// <summary>Number of tag entries in the layout JSON.</summary>
        public int tagCount;
    }

    /// <summary>
    /// Request body for <c>POST /api/apriltag-field-layouts</c>. Used for both
    /// "create new" and "edit existing" flows; sending the same name with new content
    /// overwrites in place.
    /// </summary>
    [Serializable]
    public class FieldLayoutUploadRequest
    {
        /// <summary>
        /// Friendly name (extension optional; the server appends ".json" if missing).
        /// Sanitized server-side to <c>[A-Za-z0-9._-]{1,64}</c>.
        /// </summary>
        public string name;

        /// <summary>Raw JSON content of the layout (the same shape as the bundled JSONs).</summary>
        public string content;
    }

    /// <summary>
    /// Request body for <c>PATCH /api/apriltag-field-layouts/{name}</c>.
    /// </summary>
    [Serializable]
    public class FieldLayoutRenameRequest
    {
        /// <summary>The new name (extension optional; sanitized server-side).</summary>
        public string newName;
    }

    /// <summary>
    /// Current configuration values response
    /// </summary>
    [Serializable]
    public class ConfigResponse
    {
        public bool success;
        public int teamNumber;
        public string debugIpOverride;
        public int allowedPoseResetTimeoutMs;
        public bool enableAutoStartOnBoot;
        public bool enablePassthroughStream;
        public bool enableHighQualityStream;

        /// <summary>
        /// How the camera stream and preview are rotated; see <see cref="Camera.CameraOrientation"/>.
        /// Display only: it never changes reported poses.
        /// </summary>
        public int cameraOrientation;
        public RobotToQuestModel robotToQuest;
        public StreamModeModel streamMode;
        public bool enableAprilTagDetector;
        public AprilTagDetectorModeModel aprilTagDetectorMode;
        public bool enableDebugLogging;
        public long timestamp;
    }

    /// <summary>
    /// Request to update configuration
    /// </summary>
    [Serializable]
    public class ConfigUpdateRequest
    {
        public int? TeamNumber;
        public string debugIpOverride;
        public int? AllowedPoseResetTimeoutMs;
        public bool? EnableAutoStartOnBoot;
        public bool? EnablePassthroughStream;
        public bool? EnableHighQualityStream;
        public int? CameraOrientation;
        public RobotToQuestModel RobotToQuest;
        public StreamModeModel StreamMode;
        public bool? EnableAprilTagDetector;
        public AprilTagDetectorModeModel AprilTagDetectorMode;
        public bool? EnableDebugLogging;
    }

    /// <summary>
    /// Simple success/failure response
    /// </summary>
    [Serializable]
    public class SimpleResponse
    {
        public bool success;
        public string message;
    }

    /// <summary>
    /// Response for log retrieval
    /// </summary>
    [Serializable]
    public class LogsResponse
    {
        public bool success;
        public List<LogCollector.LogEntry> logs;
    }

    /// <summary>
    /// Response for system information
    /// </summary>
    [Serializable]
    public class SystemInfoResponse
    {
        public string appName;
        public string version;
        public string unityVersion;
        public string buildDate;
        public string platform;
        public string deviceModel;
        public string operatingSystem;
        public int connectedClients;
        public int serverPort;
        public long timestamp;
    }
}
