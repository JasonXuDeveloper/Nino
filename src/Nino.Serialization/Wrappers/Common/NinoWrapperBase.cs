using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public abstract class NinoWrapperBase<T> : INinoWrapper<T>, INinoWrapper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public abstract void Serialize(T val, ref Writer writer);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public abstract T Deserialize(Reader reader);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public abstract int GetSize(T val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(object val, ref Writer writer)
        {
            Serialize((T)val, ref writer);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object INinoWrapper.Deserialize(Reader reader)
        {
            return Deserialize(reader);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSize(object val)
        {
            return GetSize((T)val);
        }
    }
}