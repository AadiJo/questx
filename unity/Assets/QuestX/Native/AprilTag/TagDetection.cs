namespace QuestX.Native.AprilTag
{
    /// <summary>
    /// Managed copy of one tag36h11 detection, independent of the detector that produced it.
    /// Corners and center use libapriltag's conventions whichever backend ran: corner order
    /// matches <see cref="AprilTagDetection"/> (and therefore
    /// <c>AprilTagFieldLayout.GetTagCorners</c>), and pixel (0, 0)'s center is at (0.5, 0.5).
    /// The image has a top-left origin (the passthrough readback is flipped before detection).
    /// </summary>
    public readonly struct TagDetection
    {
        public int Id { get; }

        /// <summary>Payload bits corrected while decoding.</summary>
        public int Hamming { get; }

        /// <summary>
        /// Decode quality. libapriltag reports its decision margin; Napa reports the grey-level
        /// contrast between the tag's white and black cells. Compare values within one backend only.
        /// </summary>
        public float DecisionMargin { get; }

        /// <summary>Projective center of the tag (the intersection of its diagonals).</summary>
        public Point2D Center { get; }

        public Point2D CornerBottomRight0 { get; }
        public Point2D CornerBottomLeft1 { get; }
        public Point2D CornerUpperLeft2 { get; }
        public Point2D CornerUpperRight3 { get; }

        public TagDetection(
            int id,
            int hamming,
            float decisionMargin,
            Point2D center,
            Point2D corner0,
            Point2D corner1,
            Point2D corner2,
            Point2D corner3
        )
        {
            Id = id;
            Hamming = hamming;
            DecisionMargin = decisionMargin;
            Center = center;
            CornerBottomRight0 = corner0;
            CornerBottomLeft1 = corner1;
            CornerUpperLeft2 = corner2;
            CornerUpperRight3 = corner3;
        }

        /// <summary>Copies a libapriltag detection before its native result array is freed.</summary>
        public static TagDetection From(AprilTagDetection detection) =>
            new TagDetection(
                detection.Id,
                detection.Hamming,
                detection.DecisionMargin,
                detection.Center,
                detection.CornerBottomRight0,
                detection.CornerBottomLeft1,
                detection.CornerUpperLeft2,
                detection.CornerUpperRight3
            );

        /// <summary>All four corners in the same order as the individual corner properties.</summary>
        public Point2D[] GetCorners() =>
            new[] { CornerBottomRight0, CornerBottomLeft1, CornerUpperLeft2, CornerUpperRight3 };
    }
}
