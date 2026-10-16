namespace QuestX.VioQuality
{
    /// <summary>One /QuestX/v2/vioQuality report (see MeasurementWireFormat.WriteVioQuality).</summary>
    public struct VioQualitySample
    {
        public DriftResult Drift;
        public uint TrackingEpoch;
        public bool PositionTracked;
        public bool PositionValid;
        public bool OrientationTracked;
        public double AngularSpeedRadPerSec;
        public double LinearSpeedMetersPerSec;
        public int Rays;
        public double NearFraction;
    }
}
