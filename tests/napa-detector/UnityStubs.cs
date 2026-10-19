// Minimal, functional stand-ins for the Unity APIs that the detector backends touch, so the
// real QuestX sources compile and run against the bundled native libraries without Unity.
// Semantics follow Unity where the backends depend on them (Reinterpret lengths, synchronous
// Complete, persistent allocations); everything else is omitted.
using System;
using System.Runtime.InteropServices;

namespace UnityEngine
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Color32
    {
        public byte r, g, b, a;

        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }
}

namespace Unity.Burst
{
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class BurstCompileAttribute : Attribute { }
}

namespace Unity.Collections
{
    public enum Allocator
    {
        None,
        Temp,
        TempJob,
        Persistent,
    }

    public enum NativeArrayOptions
    {
        UninitializedMemory,
        ClearMemory,
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ReadOnlyAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class WriteOnlyAttribute : Attribute { }

    public unsafe struct NativeArray<T> : IDisposable
        where T : unmanaged
    {
        internal void* buffer;
        internal int length;
        internal bool owned;

        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            buffer = NativeMemory.Alloc((nuint)length, (nuint)sizeof(T));
            if (options == NativeArrayOptions.ClearMemory)
                NativeMemory.Clear(buffer, (nuint)(length * sizeof(T)));
            this.length = length;
            owned = true;
        }

        public bool IsCreated => buffer != null;
        public int Length => length;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)length)
                    throw new IndexOutOfRangeException();
                return ((T*)buffer)[index];
            }
            set
            {
                if ((uint)index >= (uint)length)
                    throw new IndexOutOfRangeException();
                ((T*)buffer)[index] = value;
            }
        }

        public NativeArray<U> Reinterpret<U>(int expectedTypeSize)
            where U : unmanaged
        {
            if (expectedTypeSize != sizeof(T))
                throw new InvalidOperationException("Reinterpret size mismatch");
            return new NativeArray<U>
            {
                buffer = buffer,
                length = length * sizeof(T) / sizeof(U),
                owned = false,
            };
        }

        public void Dispose()
        {
            if (!owned)
                throw new InvalidOperationException("Disposing a NativeArray view");
            NativeMemory.Free(buffer);
            buffer = null;
            length = 0;
        }
    }
}

namespace Unity.Collections.LowLevel.Unsafe
{
    public static unsafe class NativeArrayUnsafeUtility
    {
        public static void* GetUnsafeReadOnlyPtr<T>(this NativeArray<T> array)
            where T : unmanaged => array.buffer;

        public static NativeArray<T> ConvertExistingDataToNativeArray<T>(void* data, int length, Allocator allocator)
            where T : unmanaged => new NativeArray<T> { buffer = data, length = length, owned = false };
    }
}

namespace Unity.Jobs
{
    public interface IJobParallelFor
    {
        void Execute(int index);
    }

    public struct JobHandle
    {
        public void Complete() { }
    }

    public static class IJobParallelForExtensions
    {
        public static JobHandle Schedule<T>(this T job, int arrayLength, int innerloopBatchCount)
            where T : struct, IJobParallelFor
        {
            for (int i = 0; i < arrayLength; i++)
                job.Execute(i);
            return default;
        }
    }
}

namespace QuestX.Utils
{
    public static class QueuedLogger
    {
        public static void Log(string message) => Console.WriteLine(message);

        public static void LogWarning(string message) => Console.WriteLine($"warning: {message}");

        public static void LogError(string message) => Console.WriteLine($"error: {message}");
    }
}
