using System;
using QuestX.Camera;
using QuestX.Core;
using SQLite;

namespace QuestX.Config
{
    public class Config
    {
        public class Network
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// The team number used for connecting to NetworkTables
            /// Cannot be combined with <see cref="DebugIpOverride"/> at the same time.
            /// </summary>
            public int TeamNumber { get; set; } = QuestXConstants.Network.DEFAULT_TEAM_NUMBER;

            /// <summary>
            /// An optional value that allows NetworkTables to bypass FIRST's
            /// <see href="https://docs.wpilib.org/en/stable/docs/networking/networking-introduction/ip-configurations.html">IP configuration</see>
            /// and manually specify the IP of a NetworkTables server. This is intended to only be used for debugging.
            /// Cannot be combined with <see cref="TeamNumber"/> at the same time.
            /// </summary>
            public string DebugIpOverride { get; set; } = "";

            /// <summary>
            /// How stale a request to reset the Quest's pose can be in milliseconds
            /// </summary>
            public int AllowedPoseResetTimeoutMs { get; set; } = 120;
        }

        /// <summary>
        /// Join table that stores ignored AprilTag IDs (blacklist) for the single AprilTag config row.
        /// Empty = detect every tag the camera sees.
        /// </summary>
        public class AprilTagIgnoredId
        {
            [PrimaryKey, AutoIncrement]
            public int ID { get; set; }

            /// <summary>
            /// Reference to the parent AprilTag config (we use ID=1 for the single config row)
            /// </summary>
            public int AprilTagConfigId { get; set; }

            /// <summary>
            /// The AprilTag ID to ignore. Detections with this ID are dropped before the
            /// PoseLib solver runs. tag36h11 supports 587 unique IDs but FRC only uses
            /// the first ~40, so the UI clamps user input to [0, 50] for headroom.
            /// </summary>
            public int IgnoredId { get; set; }
        }

        public class System
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// Whether the headset should automatically start the QuestX application when it turns on
            /// </summary>
            public bool EnableAutoStartOnBoot { get; set; } = true;
        }

        public class Camera
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// Whether the passthrough camera should be streamed over NT and WebUI
            /// </summary>
            public bool EnablePassthroughStream { get; set; } = false;

            /// <summary>
            /// The width of the stream in pixels
            /// </summary>
            public int PassthroughStreamWidth { get; set; } = 320;

            /// <summary>
            /// The height of the stream in pixels
            /// </summary>
            public int PassthroughStreamHeight { get; set; } = 240;

            /// <summary>
            /// The framerate of the stream in frames per second
            /// </summary>
            public int PassthroughStreamFramerate { get; set; } = 24;

            /// <summary>
            /// JPEG compression quality (1-100). Higher values mean better quality and larger files.
            /// </summary>
            public int PassthroughStreamQuality { get; set; } = 75;

            /// <summary>
            /// Whether to allow high-resolution stream modes (greater than 640x480).
            /// </summary>
            public bool EnableHighQualityStreams { get; set; } = false;

            /// <summary>
            /// How the headset is rolled on its mount, stored as a <see cref="CameraOrientation"/>.
            /// Streams, previews, and tag angles are rotated to match; poses never are (the mount
            /// roll belongs in <see cref="RobotMount"/>). Default is upright.
            /// </summary>
            public int Orientation { get; set; } = (int)CameraOrientation.NORMAL;
        }

        /// <summary>
        /// ApriTag detection configuration.
        /// </summary>
        /// <remarks>
        /// NOTE: ignored IDs (blacklist) are stored in a separate table <c>AprilTagIgnoredId</c>.
        /// See <see cref="ConfigManager"/> to read/write.
        /// </remarks>
        public class AprilTag
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// Whether the AprilTag detector is enabled. Default is true.
            /// </summary>
            public bool EnableAprilTagDetector { get; set; } = true;

            /// <summary>
            /// The mode to detect using.
            /// </summary>
            /// <remarks>
            /// <list type="table">
            /// <item>
            /// <term><c>TRADITIONAL</c></term>
            /// <description>Uses traditional PnP solving for tag detection.</description>
            /// </item>
            /// <item>
            /// <term><c>ANCHOR_ENHANCED</c></term>
            /// <description>Uses PnP solving combined with Meta Quest spatial anchors for enhanced performance.</description>
            /// </item>
            /// </list>
            /// Default is <c>TRADITIONAL</c>.
            /// </remarks>
            public int AprilTagDetectorMode { get; set; } =
                (int)Config.AprilTagDetectorMode.DetectionMode.TRADITIONAL;

            /// <summary>
            /// The width of the detection region in pixels. Default is 640.
            /// </summary>
            public int AprilTagDetectorWidth { get; set; } = 1280;

            /// <summary>
            /// The height of the detection region in pixels. Default is 480.
            /// </summary>
            public int AprilTagDetectorHeight { get; set; } = 1280;

            /// <summary>
            /// The detection framerate in frames per second. Default is 30.
            /// </summary>
            public int AprilTagDetectorFramerate { get; set; } = 30;

            /// <summary>
            /// Maximum detection distance in meters. Default is 4.0 meters.
            /// </summary>
            public double AprilTagDetectorMaxDistance { get; set; } = 4.0;

            /// <summary>
            /// Minimum number of tags required to report a valid pose. Default is 1.
            /// </summary>
            public int AprilTagDetectorMinimumNumberOfTags { get; set; } = 1;

            /// <summary>
            /// Filename of the AprilTag field-layout JSON to load at startup. The file is
            /// resolved against the bundled <c>StreamingAssets/apriltag/fieldlayouts</c>
            /// directory and the user-uploaded
            /// <c>persistentDataPath/apriltag/fieldlayouts-custom</c> directory.
            ///
            /// Changes to this value take effect on app restart only; AprilTagFieldLayout
            /// caches the loaded data for field-relative solves. A live swap would mix
            /// measurements from two field definitions and is intentionally avoided.
            /// </summary>
            public string AprilTagFieldLayoutFile { get; set; } =
                QuestXConstants.AprilTag.DEFAULT_FIELD_LAYOUT_FILE;

            /// <summary>
            /// Legacy Quest-side fusion preset retained so stored configuration remains
            /// downgrade-compatible. New robot-side fusion ignores this value.
            /// </summary>
            public int AprilTagConfidencePreset { get; set; } = 1;

            /// <summary>
            /// Legacy Quest-side fusion noise scale retained for downgrade compatibility.
            /// New robot-side fusion ignores this value.
            /// </summary>
            public double AprilTagNoiseScale { get; set; } = 1.0;

            /// <summary>
            /// Tag detector backend, a <see cref="TagDetectorBackend"/> value. Rows written
            /// before this column existed read as 0, which is <c>NAPA</c>.
            /// </summary>
            public int AprilTagDetectorBackend { get; set; } = (int)TagDetectorBackend.NAPA;
        }

        /// <summary>
        /// Where the headset sits on the robot. Published poses are the robot's pose,
        /// so robot code must not apply this transform a second time.
        /// </summary>
        public class RobotMount
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// Headset center-eye position forward of the robot origin, in meters.
            /// </summary>
            public double RobotToQuestXMeters { get; set; }

            /// <summary>
            /// Headset center-eye position left of the robot origin, in meters.
            /// </summary>
            public double RobotToQuestYMeters { get; set; }

            /// <summary>
            /// Headset center-eye position above the robot origin, in meters.
            /// </summary>
            public double RobotToQuestZMeters { get; set; }

            /// <summary>
            /// Headset roll relative to the robot, in degrees (counter-clockwise positive).
            /// </summary>
            public double RobotToQuestRollDegrees { get; set; }

            /// <summary>
            /// Headset pitch relative to the robot, in degrees (WPILib convention: positive tilts the front down).
            /// </summary>
            public double RobotToQuestPitchDegrees { get; set; }

            /// <summary>
            /// Headset yaw relative to the robot, in degrees (counter-clockwise positive).
            /// </summary>
            public double RobotToQuestYawDegrees { get; set; }
        }

        public class Logging
        {
            /// <summary>
            /// The ID to be used as the primary key of this column in the database
            /// </summary>
            [PrimaryKey]
            public int ID { get; set; }

            /// <summary>
            /// Whether debug logging for NetworkTables should be logged to the Unity and WebUI consoles.
            /// </summary>
            public bool EnableDebugLogging { get; set; } = false;
        }

        /// <summary>
        /// Represents a video stream mode configuration with resolution and framerate.
        /// </summary>
        public readonly struct StreamMode
        {
            /// <summary>
            /// The image width in pixels.
            /// </summary>
            public int Width { get; }

            /// <summary>
            /// The image height in pixels.
            /// </summary>
            public int Height { get; }

            /// <summary>
            /// The stream's frames per second.
            /// </summary>
            public int Framerate { get; }

            /// <summary>
            /// JPEG compression quality (1-100). Higher values mean better quality and larger files.
            /// </summary>
            public int Quality { get; }

            /// <summary>
            /// Create a new stream mode.
            /// </summary>
            /// <param name="width">The image width in pixels.</param>
            /// <param name="height">The image height in pixels.</param>
            /// <param name="framerate">The stream's frames per second.</param>
            /// <param name="quality">JPEG compression quality (1-100).</param>
            public StreamMode(int width, int height, int framerate, int quality)
            {
                Width = width;
                Height = height;
                Framerate = framerate;
                Quality = quality;
            }

            /// <summary>
            /// Provide string description of stream mode.
            /// </summary>
            public override string ToString()
            {
                return $"{Width}x{Height}@{Framerate}fps Quality: {Quality}";
            }
        }

        /// <summary>
        /// Which native library finds tag36h11 tags in the passthrough frames. Both feed the
        /// same PoseLib solve with identical corner conventions.
        /// </summary>
        public enum TagDetectorBackend
        {
            /// <summary>
            /// Napa sparse-sampling detector: several times faster than libapriltag, finds
            /// smaller tags, and corrects up to 3 payload bits.
            /// </summary>
            NAPA = 0,

            /// <summary>
            /// University of Michigan libapriltag (quad decimation 2, 2 corrected bits).
            /// </summary>
            APRILTAG = 1,
        }

        /// <summary>
        /// The robot-to-headset transform as stored and edited: translation in meters and
        /// roll, pitch, yaw in degrees, all in the robot's WPILib frame.
        /// </summary>
        public readonly struct RobotToQuest
        {
            /// <summary>Largest accepted translation on any axis, in meters.</summary>
            public const double MaxTranslationMeters = 2.0;

            public double XMeters { get; }
            public double YMeters { get; }
            public double ZMeters { get; }
            public double RollDegrees { get; }
            public double PitchDegrees { get; }
            public double YawDegrees { get; }

            public RobotToQuest(
                double xMeters,
                double yMeters,
                double zMeters,
                double rollDegrees,
                double pitchDegrees,
                double yawDegrees
            )
            {
                XMeters = xMeters;
                YMeters = yMeters;
                ZMeters = zMeters;
                RollDegrees = rollDegrees;
                PitchDegrees = pitchDegrees;
                YawDegrees = yawDegrees;
            }

            /// <summary>
            /// True when every value is finite, each translation is within
            /// <see cref="MaxTranslationMeters"/>, and each angle is within [-180, 180].
            /// </summary>
            public bool IsValid(out string reason)
            {
                double[] translation = { XMeters, YMeters, ZMeters };
                double[] angles = { RollDegrees, PitchDegrees, YawDegrees };
                foreach (double value in translation)
                {
                    if (!double.IsFinite(value) || Math.Abs(value) > MaxTranslationMeters)
                    {
                        reason =
                            $"Robot-to-Quest translation must be within ±{MaxTranslationMeters} m";
                        return false;
                    }
                }
                foreach (double value in angles)
                {
                    if (!double.IsFinite(value) || Math.Abs(value) > 180)
                    {
                        reason = "Robot-to-Quest angles must be within ±180°";
                        return false;
                    }
                }
                reason = "";
                return true;
            }

            public bool Equals(RobotToQuest other) =>
                XMeters == other.XMeters
                && YMeters == other.YMeters
                && ZMeters == other.ZMeters
                && RollDegrees == other.RollDegrees
                && PitchDegrees == other.PitchDegrees
                && YawDegrees == other.YawDegrees;

            public override string ToString() =>
                $"({XMeters:F3}, {YMeters:F3}, {ZMeters:F3}) m, "
                + $"roll {RollDegrees:F1}°, pitch {PitchDegrees:F1}°, yaw {YawDegrees:F1}°";
        }

        public readonly struct AprilTagDetectorMode
        {
            public DetectionMode Mode { get; }

            public enum DetectionMode
            {
                /// <summary>
                /// Uses traditional PnP solving for tag detection.
                /// </summary>
                TRADITIONAL = 0,

                /// <summary>
                /// Uses PnP solving combined with Meta Quest spatial anchors for enhanced performance.
                /// </summary>
                ANCHOR_ENHANCED = 1,
            }

            /// <summary>
            /// The width of the detection region in pixels.
            /// </summary>
            public int Width { get; }

            /// <summary>
            /// The height of the detection region in pixels.
            /// </summary>
            public int Height { get; }

            /// <summary>
            /// The detection framerate in frames per second.
            /// </summary>
            public int Framerate { get; }

            /// <summary>
            /// Array of AprilTag IDs to ignore (blacklist). Empty array detects every tag.
            /// FRC uses the first ~40 IDs of tag36h11; the UI clamps entries to [0, 50].
            /// </summary>
            public int[] IgnoredIds { get; }

            /// <summary>
            /// Maximum detection distance in meters.
            /// </summary>
            public double MaxDistance { get; }

            /// <summary>
            /// Minimum number of tags required to report a valid pose.
            /// </summary>
            public int MinimumNumberOfTags { get; }

            /// <summary>
            /// Creates a new AprilTag detector mode configuration.
            /// </summary>
            /// <param name="mode">The detection mode to use</param>
            /// <param name="width">The width of the detection region in pixels.</param>
            /// <param name="height">The height of the detection region in pixels.</param>
            /// <param name="framerate">The detection framerate in frames per second.</param>
            /// <param name="ignoredIds">Array of AprilTag IDs to ignore. Empty array detects every tag.</param>
            /// <param name="maxDistance">Maximum detection distance in meters.</param>
            /// <param name="minimumNumberOfTags">Minimum number of tags required to report a valid pose.</param>
            public AprilTagDetectorMode(
                DetectionMode mode,
                int width,
                int height,
                int framerate,
                int[] ignoredIds,
                double maxDistance,
                int minimumNumberOfTags
            )
            {
                Mode = mode;
                Width = width;
                Height = height;
                Framerate = framerate;
                IgnoredIds = ignoredIds;
                MaxDistance = maxDistance;
                MinimumNumberOfTags = minimumNumberOfTags;
            }
        }
    }
}
