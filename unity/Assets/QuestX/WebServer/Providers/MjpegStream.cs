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
}
