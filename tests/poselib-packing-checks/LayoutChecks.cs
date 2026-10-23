using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.Utils;

internal static class LayoutChecks
{
    public static async Task<int> Run()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            assertions++;
        }
        AprilTagFieldEntry Entry(int id, double x) => new()
        {
            ID = id, Pose = new Pose3d(x, 2, 3, new Rotation3d(0.23, -0.45, 0.67)),
        };
        var original = Entry(7, 1);
        var layout = new AprilTagFieldLayout(0.4) { Tags = new List<AprilTagFieldEntry> { original } };
        var first = layout.GetTagCorners(7);
        var second = layout.GetTagCorners(7);
        Check(!ReferenceEquals(first, second) && ReferenceEquals(first[0], second[0]),
            "cached immutable geometry is reused, public arrays are independent");
        first[0] = Translation3d.Zero;
        Check(ReferenceEquals(layout.GetTagCorners(7)[0], second[0]), "public array mutation cannot corrupt cache");

        var packed = Enumerable.Repeat(-100.0, 16).ToArray();
        Check(layout.TryCopyTagCorners(7, packed, 2), "copy known ID");
        Check(packed[0] == -100 && packed[1] == -100 && packed[14] == -100 && packed[15] == -100,
            "packed copy honors destination offset and extent");
        for (int i = 0; i < 4; i++)
            Check(packed[2 + i * 3] == second[i].X && packed[3 + i * 3] == second[i].Y && packed[4 + i * 3] == second[i].Z,
                "packed copy matches owned corners");
        var saved = (double[])packed.Clone();
        Check(!layout.TryCopyTagCorners(999, packed, 2) && packed.SequenceEqual(saved)
            && layout.GetTagCorners(999).Length == 0, "unknown ID leaves packed destination untouched");

        // A hot copy performs no geometry work or managed allocation after the cache is warm.
        for (int i = 0; i < 100; i++) layout.TryCopyTagCorners(7, packed, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) layout.TryCopyTagCorners(7, packed, 2);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "warm packed field-corner path allocates zero bytes");

        original.Pose = Entry(7, 4).Pose;
        var moved = layout.GetTagCorners(7);
        Check(Math.Abs(moved[0].X - second[0].X - 3) < 1e-12, "in-place pose replacement invalidates cached corners");
        original.ID = 8;
        Check(layout.GetTagCorners(7).Length == 0 && layout.GetTagCorners(8)[0].X == moved[0].X,
            "in-place ID edit does not retain old ID geometry");
        layout.Tags[0] = Entry(8, 10);
        var replaced = layout.GetTagCorners(8);
        Check(Math.Abs(replaced[0].X - moved[0].X - 6) < 1e-12, "same-list entry replacement invalidates cache");
        layout.Tags.Insert(0, Entry(8, 20));
        var duplicate = layout.GetTagCorners(8);
        Check(Math.Abs(duplicate[0].X - replaced[0].X - 10) < 1e-12, "duplicate-ID lookup still chooses first matching entry");
        layout.Tags.Reverse();
        Check(layout.GetTagCorners(8)[0].X == replaced[0].X, "same-list reordering honors current first match");
        layout.Tags.Clear();
        Check(layout.GetTagCorners(8).Length == 0, "same-list removal cannot return stale cached ID");
        layout.Tags.Add(Entry(8, 30));
        var added = layout.GetTagCorners(8);
        Check(Math.Abs(added[0].X - replaced[0].X - 20) < 1e-12, "same-list addition refreshes geometry");

        // Replacing Tags clears the cache even when entries/poses happen to be shared.
        layout.Tags = new List<AprilTagFieldEntry>(layout.Tags);
        var newList = layout.GetTagCorners(8);
        Check(newList[0].X == added[0].X && !ReferenceEquals(newList[0], added[0]), "Tags replacement rebuilds cache");
        var independent = new AprilTagFieldLayout(0.8) { Tags = layout.Tags };
        Check(Math.Abs(independent.GetTagCorners(8)[0].GetDistance(newList[0]) - Math.Sqrt(0.08)) < 1e-12,
            "layout instances do not share cached corners or tag size");
        layout.Tags = null;
        Check(layout.GetTagCorners(8).Length == 0, "null Tags does not expose old cached geometry");

        // Exercise the real public loader, including validation failures retaining prior data.
        string directory = Path.Combine(Path.GetTempPath(), "questx-packing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        FileManager.Root = directory;
        string path = Path.Combine(directory, "layout.json");
        void Write(AprilTagFieldEntry[] entries) => File.WriteAllText(path, JsonConvert.SerializeObject(new
        {
            tags = entries.Select(e => new
            {
                ID = e.ID,
                pose = new
                {
                    translation = new { x = e.Pose.X, y = e.Pose.Y, z = e.Pose.Z },
                    rotation = new { quaternion = new { W = e.Pose.Rotation.Quaternion.W,
                        X = e.Pose.Rotation.Quaternion.X, Y = e.Pose.Rotation.Quaternion.Y, Z = e.Pose.Rotation.Quaternion.Z } },
                },
            }),
            field = new { length = 16, width = 8 },
        }));
        try
        {
            Write(new[] { Entry(7, 1), Entry(8, 2) });
            await layout.LoadJsonFromFileAsync("layout.json");
            var loaded = layout.GetTagCorners(7);
            Check(layout.IsLoaded && layout.ContainsId(7) && layout.ContainsId(8) && layout.LoadedFile == "layout.json",
                "valid load retains ID validation and metadata");
            Write(new[] { Entry(7, 40), Entry(9, 2) });
            await layout.LoadJsonFromFileAsync("layout.json");
            var reloaded = layout.GetTagCorners(7);
            Check(layout.IsLoaded && !layout.ContainsId(8) && layout.ContainsId(9)
                && Math.Abs(reloaded[0].X - loaded[0].X - 39) < 1e-12, "reload invalidates corners and rebuilds known IDs");
            Check(layout.GetTagCorners(8).Length == 0, "reload removes old ID geometry");
            var validTags = layout.Tags;
            foreach (string invalid in new[] { "{broken", "{\"tags\":[]}", "{\"tags\":[{\"ID\":7}],\"field\":{\"length\":16,\"width\":8}}" })
            {
                File.WriteAllText(path, invalid);
                await layout.LoadJsonFromFileAsync("layout.json");
                Check(!layout.IsLoaded && layout.LoadError.Length > 0 && ReferenceEquals(validTags, layout.Tags)
                    && ReferenceEquals(reloaded[0], layout.GetTagCorners(7)[0]), "failed load retains previous data and corners");
            }
            Write(new[] { Entry(7, 50), Entry(7, 60) });
            await layout.LoadJsonFromFileAsync("layout.json");
            Check(ReferenceEquals(validTags, layout.Tags) && !layout.IsLoaded, "duplicate-ID load is still rejected");
            File.Delete(path);
            await layout.LoadJsonFromFileAsync("layout.json");
            Check(ReferenceEquals(validTags, layout.Tags) && ReferenceEquals(reloaded[0], layout.GetTagCorners(7)[0]),
                "missing-file load retains previous geometry");
            Write(new[] { Entry(7, 70) });
            await layout.LoadJsonFromFileAsync("layout.json");
            Check(layout.IsLoaded && Math.Abs(layout.GetTagCorners(7)[0].X - loaded[0].X - 69) < 1e-12,
                "successful reload after failure refreshes geometry");
        }
        finally { Directory.Delete(directory, true); }
        return assertions;
    }
}
