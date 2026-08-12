using System;

namespace QuestX.Native.NTCore
{
    public class RawPublisher
    {
        private readonly uint handle;

        internal RawPublisher(uint handle)
        {
            this.handle = handle;
        }

        public unsafe bool Set(byte[] value)
        {
            return Set(value, 0);
        }

        /// <summary>
        /// Publishes a raw value with an explicit NetworkTables timestamp.
        /// </summary>
        /// <param name="value">The raw value to publish.</param>
        /// <param name="timestampMicros">The NT timestamp in microseconds.</param>
        /// <returns>True when the value was successfully published.</returns>
        public unsafe bool Set(byte[] value, long timestampMicros)
        {
            fixed (byte* ptr = value)
            {
                return NtCoreNatives.NT_SetRaw(handle, timestampMicros, ptr, (UIntPtr)value.Length)
                    != 0;
            }
        }
    }
}
