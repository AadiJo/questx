using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuestX.WebServer
{
    /// <summary>
    /// A JPEG-encoded video frame with frame number.
    /// </summary>
    public struct EncodedFrame
    {
        /// <summary>
        /// The frame number. Corresponds with Time.frameCount.
        /// </summary>
        public readonly int frameNumber;

        /// <summary>
        /// A JPEG encoded frame.
        /// </summary>
        public readonly byte[] frameData;

        /// <summary>
        /// Creates a new encoded frame.
        /// </summary>
        /// <param name="frameNumber">Unity frame count.</param>
        /// <param name="frameData">JPEG encoded image data.</param>
        public EncodedFrame(int frameNumber, byte[] frameData)
        {
            this.frameNumber = frameNumber;
            this.frameData = frameData;
        }
    }

    /// <summary>
    /// Writes the latest encoded frame to one MJPEG client, paced against a monotonic clock.
    /// </summary>
    internal static class MjpegStream
    {
        /// <summary>
        /// MIME boundary token for multipart responses.
        /// </summary>
        public const string Boundary = "frame";

        /// <summary>
        /// Part header up to the Content-Length value.
        /// </summary>
        private static readonly byte[] HeaderStartBytes = Encoding.UTF8.GetBytes(
            "\r\n--" + Boundary + "\r\n" + "Content-Type: image/jpeg\r\n" + "Content-Length: "
        );

        /// <summary>
        /// Part header after the Content-Length value.
        /// </summary>
        private static readonly byte[] HeaderEndBytes = Encoding.UTF8.GetBytes("\r\n\r\n");

        /// <summary>
        /// Digits in int.MaxValue, the longest Content-Length a byte array can need.
        /// </summary>
        private const int MaxLengthDigits = 10;

        /// <summary>
        /// Streams frames until the source becomes unavailable, the client disconnects or
        /// the request is cancelled.
        /// </summary>
        public static Task StreamAsync(
            Stream output,
            Func<EncodedFrame> currentFrame,
            Func<bool> isAvailable,
            Func<int> maxFrameRate,
            CancellationToken cancellationToken
        )
        {
            Stopwatch clock = Stopwatch.StartNew();
            return StreamAsync(
                output,
                currentFrame,
                isAvailable,
                maxFrameRate,
                () => clock.Elapsed,
                Task.Delay,
                cancellationToken
            );
        }

        /// <summary>
        /// Streams frames using the supplied monotonic clock and delay. Each poll is due one
        /// frame period after the previous poll's deadline, so write time counts toward the
        /// period. A poll that starts late (slow write or late timer) re-anchors the deadline
        /// to its start: missed periods are skipped instead of replayed as a burst.
        /// </summary>
        internal static async Task StreamAsync(
            Stream output,
            Func<EncodedFrame> currentFrame,
            Func<bool> isAvailable,
            Func<int> maxFrameRate,
            Func<TimeSpan> now,
            Func<TimeSpan, CancellationToken, Task> delay,
            CancellationToken cancellationToken
        )
        {
            // One header buffer per client; only the length digits change between frames.
            byte[] header = new byte[
                HeaderStartBytes.Length + MaxLengthDigits + HeaderEndBytes.Length
            ];
            Buffer.BlockCopy(HeaderStartBytes, 0, header, 0, HeaderStartBytes.Length);

            int lastFrame = 0;
            TimeSpan deadline = now();
            try
            {
                while (!cancellationToken.IsCancellationRequested && isAvailable())
                {
                    // A late timer wakeup or slow write leaves a stale deadline; pace from now
                    // so the next poll is a full period away instead of immediate.
                    TimeSpan started = now();
                    if (deadline < started)
                    {
                        deadline = started;
                    }

                    EncodedFrame frame = currentFrame();
                    if (lastFrame < frame.frameNumber && frame.frameData != null)
                    {
                        await WriteFrameAsync(output, header, frame.frameData, cancellationToken);
                        // Don't re-send the same frame
                        lastFrame = frame.frameNumber;
                    }

                    TimeSpan period = TimeSpan.FromTicks(
                        TimeSpan.TicksPerSecond / Math.Max(1, maxFrameRate())
                    );
                    TimeSpan current = now();
                    deadline += period;
                    if (deadline > current)
                    {
                        await delay(deadline - current, cancellationToken);
                    }
                }
            }
            catch (IOException)
            {
                // The client probably disconnected - exit gracefully
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The request ended while writing or waiting
            }
        }

        /// <summary>
        /// Writes one multipart part directly from the immutable JPEG array and flushes once.
        /// </summary>
        private static async Task WriteFrameAsync(
            Stream output,
            byte[] header,
            byte[] jpegData,
            CancellationToken cancellationToken
        )
        {
            Utf8Formatter.TryFormat(
                jpegData.Length,
                new Span<byte>(header, HeaderStartBytes.Length, MaxLengthDigits),
                out int digits
            );
            int headerLength = HeaderStartBytes.Length + digits;
            Buffer.BlockCopy(HeaderEndBytes, 0, header, headerLength, HeaderEndBytes.Length);
            headerLength += HeaderEndBytes.Length;

            await output.WriteAsync(header, 0, headerLength, cancellationToken);
            await output.WriteAsync(jpegData, 0, jpegData.Length, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }
}
