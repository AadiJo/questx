// Counted Unity/OpenXR stand-ins, not engine or native runtime validation.
// System.Numerics supplies rotations; both monitor implementations use these same operations.
using QuestX.Core;
using QuestX.VioQuality;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 forward => new(0, 0, 1);
        public Vector3 normalized
        {
            get
            {
                float n = MathF.Sqrt(x * x + y * y + z * z);
                return n == 0 ? this : new(x / n, y / n, z / n);
            }
        }
        public static float Distance(Vector3 a, Vector3 b) =>
            MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }

    public struct Quaternion
    {
        public System.Numerics.Quaternion Value;
        public static Quaternion Euler(float x, float y, float z) => new()
        {
            Value = System.Numerics.Quaternion.CreateFromYawPitchRoll(y * MathF.PI / 180, x * MathF.PI / 180, z * MathF.PI / 180)
        };
        public static Quaternion Inverse(Quaternion q) => new() { Value = System.Numerics.Quaternion.Inverse(q.Value) };
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var p = System.Numerics.Vector3.Transform(new(v.x, v.y, v.z), q.Value);
            return new(p.X, p.Y, p.Z);
        }
    }

    public class Transform
    {
        public int Positions, Rotations, InversePoints, InverseDirections;
        public Vector3 Position = new(1, 2, 3);
        public Quaternion Rotation = Quaternion.Euler(10, 20, 30);
        public Vector3 Scale = new(1.5f, 0.8f, 2);
        public Vector3 position { get { Positions++; return Position; } }
        public Quaternion rotation { get { Rotations++; return Rotation; } }
        public Vector3 InverseTransformPoint(Vector3 p)
        {
            InversePoints++;
            var local = Quaternion.Inverse(Rotation) * new Vector3(p.x - Position.x, p.y - Position.y, p.z - Position.z);
            return new(local.x / Scale.x, local.y / Scale.y, local.z / Scale.z);
        }
        public Vector3 InverseTransformDirection(Vector3 p)
        {
            InverseDirections++;
            return Quaternion.Inverse(Rotation) * p;
        }
    }

    public struct Ray
    {
        public Vector3 origin, direction;
        public Ray(Vector3 p, Vector3 d) { origin = p; direction = d; }
    }
    public static class Mathf
    {
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float Sqrt(float x) => MathF.Sqrt(x);
    }
    public class GameObject
    {
        public Meta.XR.EnvironmentRaycastManager Manager = new();
        public T GetComponent<T>() where T : class => Manager as T;
        public T AddComponent<T>() where T : new() => new();
    }
}

public class OVRCameraRig
{
    public UnityEngine.Transform centerEyeAnchor = new(), trackingSpace = new();
}
public static class OVRPlugin
{
    public enum Node { Head }
    public enum Step { Render }
    public static bool Tracking = true, Valid = true, Orientation = true;
    public static bool GetNodePositionTracked(Node n) => Tracking;
    public static bool GetNodePositionValid(Node n) => Valid;
    public static bool GetNodeOrientationTracked(Node n) => Orientation;
    public static UnityEngine.Vector3 GetNodeAngularVelocity(Node n, Step s) => new(.1f, .2f, .3f);
    public static UnityEngine.Vector3 GetNodeVelocity(Node n, Step s) => new(.3f, .4f, .5f);
}

namespace Meta.XR
{
    public enum EnvironmentRaycastHitStatus { NotReady, Hit }
    public enum HitMode { All, Mixed, NotReady, Miss }
    public struct HitResult
    {
        public EnvironmentRaycastHitStatus status;
        public float normalConfidence;
        public UnityEngine.Vector3 point, normal;
    }
    public class EnvironmentRaycastManager
    {
        public static bool IsSupported = true;
        public bool isActiveAndEnabled = true;
        public HitMode Mode;
        public List<UnityEngine.Ray> Rays = new();
        public bool Raycast(UnityEngine.Ray ray, out HitResult result, float range)
        {
            if (range != QuestXConstants.VioQuality.MAX_RANGE_METERS)
                throw new Exception("Raycast range changed");
            int i = Rays.Count;
            Rays.Add(ray);
            result = new()
            {
                status = Mode == HitMode.NotReady ? EnvironmentRaycastHitStatus.NotReady : EnvironmentRaycastHitStatus.Hit,
                normalConfidence = 1,
                point = new(ray.origin.x + ray.direction.x * 2, ray.origin.y + ray.direction.y * 2, ray.origin.z + ray.direction.z * 2),
                normal = (i % 3) switch { 0 => new(1, 0, 0), 1 => new(0, 1, 0), _ => new(0, 0, 1) }
            };
            if (Mode == HitMode.Mixed)
            {
                if (i % 4 == 0) return false;
                if (i % 4 == 1) result.normalConfidence = 0;
                if (i % 4 == 2) result.point = ray.origin;
            }
            return Mode != HitMode.NotReady && Mode != HitMode.Miss;
        }
    }
}
namespace QuestX.Core
{
    public class VioReferenceFrame
    {
        public uint Epoch = 2;
        public bool PublishedTracking = true;
        public double YawCorrectionRadians = .2;
        public Vec3d Offset = new(.2, .3, .4);
    }
}
namespace QuestX.Utils { public static class QueuedLogger { public static void LogWarning(string message) { } } }
namespace QuestX.Native.NTCore { public struct PubSubOptions { public static PubSubOptions AllDefault => default; } }
