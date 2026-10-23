using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuestX.Core;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using Wpi.Proto;

namespace QuestX.QuestX.AprilTag
{
    public class AprilTagFieldLayout
    {
        private List<AprilTagFieldEntry> tags;
        private readonly Dictionary<int, (Pose3d Pose, Translation3d[] Corners)> cornerCache =
            new Dictionary<int, (Pose3d, Translation3d[])>();

        [JsonProperty("tags")]
        public List<AprilTagFieldEntry> Tags
        {
            get => tags;
            set
            {
                tags = value;
                cornerCache.Clear();
            }
        }

        [JsonProperty("field")]
        public Field2d Field { get; set; }

        /// <summary>
        /// O(1) lookup of tag IDs known to the loaded layout. Rebuilt every successful
        /// load (<see cref="TryLoadFrom"/>). The detection pipeline calls
        /// <see cref="ContainsId"/> on every detected tag to drop false-positive IDs
        /// (e.g. tag36h11 noise that decodes to ID 554 when no such tag is on the field).
        /// Feeding such IDs into <c>PoseLibSolver</c> would mismatch the 2D/3D corner
        /// buffer lengths and produce a garbage pose tens of meters from the field.
        /// </summary>
        private HashSet<int> knownIds = new HashSet<int>();

        [JsonIgnore]
        public string LoadedFile { get; private set; } = "";

        [JsonIgnore]
        public string LoadError { get; private set; } = "Not loaded";

        [JsonIgnore]
        public bool IsLoaded => knownIds.Count > 0 && string.IsNullOrEmpty(LoadError);

        /// <summary>
        /// Represents the physical size of the AprilTags on the field
        /// </summary>
        public double TagSize { get; }

        public AprilTagFieldLayout()
            : this(QuestXConstants.AprilTag.TAG_SIZE_METERS) { }

        /// <summary>
        /// Creates a new AprilTagFieldLayout.
        /// <param name="tagSize">The size of the tags in meters (black part)</param>
        /// <see cref="LoadJsonFromFileAsync">LoadJsonFromFileAsync</see> MUST be called prior to getting data
        /// </summary>
        public AprilTagFieldLayout(double tagSize)
        {
            if (tagSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tagSize),
                    tagSize,
                    "AprilTag size must be greater than zero."
                );
            }

            TagSize = tagSize;
        }

        /// <summary>
        /// Loads a field-layout JSON file. On failure (missing file, IO error,
        /// deserialization failure, empty <c>tags</c> array), the existing
        /// <see cref="Tags"/> / <see cref="Field"/> values are left untouched.
        ///
        /// Lookup order: the user-uploaded "custom" directory
        /// (<see cref="FileManager.GetCustomFieldLayoutDir"/>) is checked first. If the
        /// file is not there, falls through to the bundled
        /// <c>StreamingAssets/apriltag/fieldlayouts/</c> directory (extracted from the
        /// APK on Android). Bundled-name shadowing is rejected at upload time, so this
        /// order does not let a custom file silently override a bundled one.
        /// </summary>
        /// <param name="fileName">The filename to load (must include extension).</param>
        public async Task LoadJsonFromFileAsync(string fileName)
        {
            // 1) Custom-uploaded JSONs live in persistentDataPath; check there first.
            string customPath = Path.Combine(FileManager.GetCustomFieldLayoutDir(), fileName);
            if (File.Exists(customPath))
            {
                TryLoadFrom(customPath);
                return;
            }

            // 2) Fall through to bundled. On Android this requires extracting the
            // JSON out of the APK first; on Editor / Standalone the StreamingAssets
            // path is directly readable.
            string bundledDir = FileManager.GetStaticFilesPath("apriltag/fieldlayouts");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                await FileManager.ExtractAndroidFileAsync(
                    fileName,
                    "apriltag/fieldlayouts",
                    bundledDir
                );
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"Failed to extract bundled field layout '{fileName}' from APK: {ex.Message}"
                );
                return;
            }
#else
            await Task.CompletedTask;
#endif
            string bundledPath = Path.Combine(bundledDir, fileName);
            TryLoadFrom(bundledPath);
        }

        /// <summary>
        /// Synchronous loader used by both the custom and bundled code paths above.
        /// Captures the parse failure modes so the caller (which is async) can return
        /// a simple bool.
        /// </summary>
        private bool TryLoadFrom(string filePath)
        {
            try
            {
                using var file = File.OpenText(filePath);
                var jsonSerializer = new JsonSerializer();
                var root = (AprilTagFieldLayout)
                    jsonSerializer.Deserialize(file, typeof(AprilTagFieldLayout));

                if (root == null || root.Tags == null || root.Tags.Count == 0)
                {
                    LoadError = "Field layout has no valid tags";
                    QueuedLogger.LogError(
                        $"Field layout file '{filePath}' deserialized to an empty / invalid layout."
                    );
                    return false;
                }

                if (root.Field == null || root.Field.X <= 0 || root.Field.Y <= 0)
                    throw new InvalidDataException(
                        "Field dimensions length/width must be positive"
                    );
                var unique = new HashSet<int>();
                foreach (var tag in root.Tags)
                {
                    if (tag == null || tag.Pose == null || !unique.Add(tag.ID))
                        throw new InvalidDataException("Missing tag pose or duplicate tag ID");
                }
                Tags = root.Tags;
                Field = root.Field;
                LoadedFile = Path.GetFileName(filePath);
                LoadError = "";

                var rebuilt = new HashSet<int>();
                foreach (var tag in Tags)
                {
                    rebuilt.Add(tag.ID);
                }
                knownIds = rebuilt;

                QueuedLogger.Log(
                    $"Loaded new AprilTagFieldLayout '{filePath}' with {Tags.Count} tags"
                );
                return true;
            }
            catch (Exception ex)
            {
                QueuedLogger.LogError(
                    $"Failed to load field layout '{filePath}': {ex.GetType().Name}: {ex.Message}"
                );
                LoadError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Gets the field-relative pose of a tag by ID, or null if the ID is not in the
        /// loaded layout. Used by the AprilTag detection pipeline to compute the true
        /// camera-to-tag distance for max-distance gating and dynamic std dev scaling.
        /// </summary>
        /// <param name="id">The ID of the tag's pose to get.</param>
        /// <returns>The tag's field-relative pose, or null if not found.</returns>
        public Pose3d GetTagPose(int id)
        {
            if (Tags == null)
                return null;
            foreach (var tag in Tags)
            {
                if (tag.ID == id)
                    return tag.Pose;
            }
            return null;
        }

        /// <summary>
        /// Fast O(1) check for whether an AprilTag ID is present in the loaded layout.
        /// Returns false before <see cref="LoadJsonFromFileAsync"/> succeeds. The detection
        /// pipeline uses this to drop false-positive detections (random noise patterns
        /// that decode to a valid tag36h11 codeword) before passing them to PoseLib.
        /// </summary>
        public bool ContainsId(int id)
        {
            return knownIds != null && knownIds.Contains(id);
        }

        /// <summary>
        /// Gets all four corners of the AprilTag in field relative space given the ID and loaded layout
        /// </summary>
        /// <param name="id">The ID of the tag's pose to get</param>
        /// <returns>An array containing four Translation3Ds of the corners</returns>
        public Translation3d[] GetTagCorners(int id)
        {
            var corners = GetCachedTagCorners(id);
            // Preserve caller ownership of the public array. Translation3d is immutable.
            return corners == null
                ? Array.Empty<Translation3d>()
                : (Translation3d[])corners.Clone();
        }

        /// <summary>
        /// Copies cached field corners into a solve-owned packed XYZ buffer, without exposing
        /// the cached array. Returns false for an unknown ID, leaving the buffer untouched.
        /// </summary>
        internal bool TryCopyTagCorners(int id, double[] destination, int offset)
        {
            var corners = GetCachedTagCorners(id);
            if (corners == null)
                return false;
            for (int i = 0; i < corners.Length; i++)
            {
                destination[offset++] = corners[i].X;
                destination[offset++] = corners[i].Y;
                destination[offset++] = corners[i].Z;
            }
            return true;
        }

        private Translation3d[] GetCachedTagCorners(int id)
        {
            // Tags and its entries are publicly mutable. Resolve the current pose first so
            // in-place edits (including ID changes, removals and reordering) cannot reuse
            // stale geometry. Pose3d and its translation/rotation values are immutable.
            var tagPose = GetTagPose(id);
            if (tagPose != null)
            {
                if (
                    cornerCache.TryGetValue(id, out var cached)
                    && ReferenceEquals(cached.Pose, tagPose)
                )
                    return cached.Corners;

                // Corners ordered to match the passthrough camera's clockwise detection order
                // (BR, BL, UL, UR); the single-tag camera-frame solve shares this order.
                var localCorners = AprilTagCameraPose.TagLocalCorners(TagSize);
                var fieldTransforms = new Translation3d[localCorners.Length];
                for (int i = 0; i < localCorners.Length; i++)
                {
                    fieldTransforms[i] = tagPose
                        .Plus(new Transform3d(localCorners[i], new Rotation3d()))
                        .Translation;
                }

                cornerCache[id] = (tagPose, fieldTransforms);
                return fieldTransforms;
            }
            // ID does not exist in our list.
            QueuedLogger.LogWarning(
                $"Attempted to get corners of non-existent ID in the current field layout! ID: {id}"
            );
            return null;
        }
    }
}
