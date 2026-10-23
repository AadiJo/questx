using System.Collections.Generic;

// Only platform and native-call boundaries are replaced. Layout, packing, geometry,
// quality, covariance and result conversion all compile from the production sources.
namespace UnityEngine { }
namespace QuestX.Camera { }
namespace Meta.XR
{
    public class PassthroughCameraAccess
    {
        public struct CameraIntrinsics
        {
            public (float x, float y) FocalLength, PrincipalPoint;
            public (int x, int y) SensorResolution;
        }
    }
}
namespace QuestX.Core
{
    public static class QuestXConstants
    {
        public static class AprilTag { public const double TAG_SIZE_METERS = 0.1651; }
    }
}
namespace QuestX.Utils
{
    public static class FileManager
    {
        public static string Root;
        public static string GetCustomFieldLayoutDir() => Root;
        public static string GetStaticFilesPath(string path) => Root;
    }
    public static class QueuedLogger
    {
        public static void Log(string message) { }
        public static void LogError(string message) { }
        public static void LogWarning(string message) { }
    }
}
namespace QuestX.Native.AprilTag
{
    public struct Point2D
    {
        public double x, y;
        public Point2D(double x, double y) { this.x = x; this.y = y; }
    }
    public class AprilTagDetection
    {
        public int Id, Hamming;
        public float DecisionMargin;
        public Point2D Center, CornerBottomRight0, CornerBottomLeft1, CornerUpperLeft2, CornerUpperRight3;
    }
    public class AprilTagDetectionResults : List<AprilTagDetection>
    {
        public int NumberOfDetections => Count;
    }
}
namespace QuestX.QuestX.Native.PoseLib
{
    public static unsafe class PoseLibNatives
    {
        public enum PoseLibCameraModelIdNative { POSELIB_CAMERA_PINHOLE = 1 }
        public struct PoseLibCameraPoseNative { public fixed double Q[4]; public fixed double T[3]; }
        public static int Calls, Status = 1;
        public static double[] Points2d, Points3d, CameraParams;
        public static ulong NumPoints, NumCameraParams;
        public static int Width, Height, Model;
        public static double Threshold;
        public static int poselib_estimate_absolute_pose_simple(
            double[] points2d, double[] points3d, ulong numPoints, int cameraModelID,
            int imageWidth, int imageHeight, double[] cameraParams, ulong numCameraParams,
            double maxReprojError, out PoseLibCameraPoseNative poseOut, out ulong numInliersOut)
        {
            Calls++;
            Points2d = points2d; Points3d = points3d; NumPoints = numPoints;
            CameraParams = cameraParams; NumCameraParams = numCameraParams;
            Width = imageWidth; Height = imageHeight; Model = cameraModelID; Threshold = maxReprojError;
            poseOut = default; poseOut.Q[0] = 1;
            numInliersOut = numPoints;
            return Status;
        }
    }
}
