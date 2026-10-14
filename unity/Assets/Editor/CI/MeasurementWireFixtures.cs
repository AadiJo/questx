using System;
using System.Globalization;
using System.IO;
using System.Linq;
using QuestX.Network;
using QuestX.QuestX.Geometry;
using UnityEngine;

namespace QuestX.Editor.CI
{
    /// <summary>Exports actual C# encoder output for Java interoperability tests.</summary>
    public static class MeasurementWireFixtures
    {
        public static void Export()
        {
            string output = Path.GetFullPath(
                Path.Combine(
                    Application.dataPath,
                    "../../questx-lib/src/test/resources/measurement-v2"
                )
            );
            Directory.CreateDirectory(output);
            var pose = new Pose3d(
                1.25,
                -2.5,
                0.75,
                new Rotation3d(new global::QuestX.QuestX.Geometry.Quaternion(0.5, 0.5, -0.5, 0.5))
            );
            var vio = new double[MeasurementWireFormat.VioLength];
            MeasurementWireFormat.WriteVio(
                vio,
                0xfedcba9876543210UL,
                0x0020000000000001L,
                12.5,
                pose,
                true,
                uint.MaxValue
            );
            Write(output, "vio", vio);
            MeasurementWireFormat.WriteVio(vio, 0, 1234567, 0.25, Pose3d.Zero, false, 0);
            Write(output, "vio-restarted", vio);
            var tags = new double[MeasurementWireFormat.AprilTagHeaderLength + 3];
            MeasurementWireFormat.WriteAprilTag(
                tags,
                42,
                987654321,
                4.5,
                pose,
                new[] { 1, 22, 586 },
                10,
                12,
                10.0 / 12,
                2.75,
                0.1,
                0.2,
                0.3,
                0.01,
                0.02,
                0.03
            );
            Write(output, "apriltag", tags);
            Debug.Log("Measurement v2 C# fixtures exported to " + output);
        }

        private static void Write(string directory, string name, double[] values)
        {
            File.WriteAllText(
                Path.Combine(directory, name + ".txt"),
                string.Join(
                    ",",
                    values.Select(value => value.ToString("R", CultureInfo.InvariantCulture))
                ) + "\n"
            );
        }
    }
}
