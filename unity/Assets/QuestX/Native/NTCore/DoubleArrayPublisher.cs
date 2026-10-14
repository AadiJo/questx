using System;

namespace QuestX.Native.NTCore
{
    /// <summary>Publishes an atomic numeric array, optionally at its capture timestamp.</summary>
    public class DoubleArrayPublisher
    {
        private readonly uint handle;

        internal DoubleArrayPublisher(uint handle)
        {
            this.handle = handle;
        }

        public unsafe bool Set(double[] value, long timestampMicros = 0)
        {
            fixed (double* ptr = value)
            {
                return NtCoreNatives.NT_SetDoubleArray(
                        handle,
                        timestampMicros,
                        ptr,
                        (UIntPtr)value.Length
                    ) != 0;
            }
        }
    }
}
