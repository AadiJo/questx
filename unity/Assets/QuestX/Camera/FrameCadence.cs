namespace QuestX.Camera
{
    /// <summary>Paces work against a monotonic clock, without sleeping after each frame.</summary>
    internal static class FrameCadence
    {
        /// <summary>
        /// Takes a due frame. Times are milliseconds; reset nextAtMs to zero on a mode change.
        /// The caller must supply a positive interval and only take frames it can process.
        /// </summary>
        public static bool TryTake(double nowMs, double intervalMs, ref double nextAtMs)
        {
            // Ignore only floating-point rounding at an exact frame boundary.
            if (nowMs + 1e-6 < nextAtMs)
                return false;
            // Keep phase when the camera grid crosses a deadline (e.g. 30 Hz to 24 fps).
            // A long stall starts fresh so old deadlines never create a catch-up burst.
            nextAtMs =
                nextAtMs <= 0 || nowMs - nextAtMs >= intervalMs
                    ? nowMs + intervalMs
                    : nextAtMs + intervalMs;
            return true;
        }
    }
}
