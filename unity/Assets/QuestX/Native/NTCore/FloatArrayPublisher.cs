using System;

namespace QuestX.Native.NTCore
{
    public class FloatArrayPublisher
    {
        private readonly uint handle;

        internal FloatArrayPublisher(uint handle)
        {
            this.handle = handle;
        }

        public unsafe bool Set(float[] value)
        {
            fixed (float* ptr = value)
            {
                return NtCoreNatives.NT_SetFloatArray(handle, 0, ptr, (UIntPtr)value.Length) != 0;
            }
        }
    }
}
