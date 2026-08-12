using Newtonsoft.Json;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    public class AprilTagFieldEntry
    {
        [JsonProperty("ID")]
        public int ID { get; set; }

        [JsonProperty("pose")]
        public Pose3d Pose { get; set; }
    }
}
