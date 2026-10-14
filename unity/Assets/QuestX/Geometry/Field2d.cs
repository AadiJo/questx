using Newtonsoft.Json;

namespace QuestX.QuestX.Geometry
{
    public class Field2d
    {
        [JsonProperty("length")]
        public double X { get; set; }

        [JsonProperty("width")]
        public double Y { get; set; }

        [JsonConstructor]
        public Field2d(double length, double width)
        {
            X = length;
            Y = width;
        }
    }
}
