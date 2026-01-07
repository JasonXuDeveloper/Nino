using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    public static class NinoMarshal
    {
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetArrayReference<T>(T[]? array)
        {
#if NET6_0_OR_GREATER
            if (array is null)
                return ref Unsafe.NullRef<T>();
        
            return ref MemoryMarshal.GetArrayDataReference(array);
#else
            return ref array.AsSpan().GetPinnableReference();
#endif

        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte GetArrayReference<T>(Array? array)
        {
#if NET6_0_OR_GREATER
            if (array is null)
                return ref Unsafe.NullRef<byte>();

            return ref MemoryMarshal.GetArrayDataReference(array);
#else
            return ref DangerousGetArrayDataReference<T>(array);
#endif
        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T DangerousGetArrayDataReference<T>(T[] array)
        {
#if NETSTANDARD2_1
            ref var src = ref Unsafe.As<T, byte>(ref Unsafe.As<byte, T>(ref Unsafe.As<LuminRawArrayData>(array).Data));
            return ref Unsafe.As<byte, T>(ref Unsafe.Add(ref src, Unsafe.SizeOf<UIntPtr>()));
#else
            return ref GetArrayReference(array);
#endif
        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte DangerousGetArrayDataReference<T>(Array array)
        {
#if NETSTANDARD2_1
            ref var src = ref Unsafe.As<T, byte>(ref Unsafe.As<byte, T>(ref Unsafe.As<LuminRawArrayData>(array).Data));
            return ref Unsafe.Add(ref src, Unsafe.SizeOf<UIntPtr>());
#else
            return ref GetArrayReference<T>(array);
#endif
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T AllocateUninitializedObject<T>()
        {
            return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateUninitializedArray<T>(int length)
        {
#if NET6_0_OR_GREATER
            return GC.AllocateUninitializedArray<T>(length);
#else
        return new T[length];
#endif
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetMethodTable(object obj)
        {
#if NET6_0_OR_GREATER
            return Unsafe.Add(ref Unsafe.As<byte, IntPtr>(ref Unsafe.As<RawData>(obj).Data), -1);
#else
        return GetMethodTable(obj.GetType());
#endif
        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetMethodTable<T>(T obj) where T : class
        {
#if NET6_0_OR_GREATER
            return Unsafe.Add(ref Unsafe.As<byte, IntPtr>(ref Unsafe.As<RawData>(obj).Data), -1);
#else
        return GetMethodTable(obj.GetType());
#endif
        
        }
    
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetMethodTable(Type type)
        {
            return type.TypeHandle.Value;
        }
    
        private sealed class RawData
        {
            public byte Data;
        }
        
        public sealed class LuminRawArrayData
        {
            public uint Length;
            public uint Rank;
            public byte Data;
        }
    }
}