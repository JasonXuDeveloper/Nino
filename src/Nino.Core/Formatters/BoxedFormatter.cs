using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace Nino.Core
{
    /// <summary>Non-generic entry point to <see cref="NinoFormatterCache{T}"/> for a runtime type.</summary>
    internal abstract class BoxedFormatter
    {
        private static readonly ConcurrentDictionary<Type, BoxedFormatter> Cache =
            new ConcurrentDictionary<Type, BoxedFormatter>();

        // A lambda rather than a method group: older compilers (Unity's) allocate a delegate per method group use.
        public static BoxedFormatter Get(Type type) => Cache.GetOrAdd(type,
            t => (BoxedFormatter)Activator.CreateInstance(typeof(BoxedFormatter<>).MakeGenericType(t)));

        public abstract byte[] Serialize(object value);
        public abstract void Serialize(object value, IBufferWriter<byte> bufferWriter);
        public abstract void Serialize(object value, ref Writer writer);
        public abstract void Deserialize(ref object value, ref Reader reader);
    }

    internal sealed class BoxedFormatter<T> : BoxedFormatter
    {
        public override byte[] Serialize(object value) => NinoSerializer.Serialize((T)value);

        public override void Serialize(object value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize((T)value, bufferWriter);

        public override void Serialize(object value, ref Writer writer) =>
            NinoTopFormatter<T>.Get.Serialize((T)value, ref writer);

        public override void Deserialize(ref object value, ref Reader reader)
        {
            T typed = value is T existing ? existing : default;
            NinoTopFormatter<T>.Get.Deserialize(ref typed, ref reader);
            value = typed;
        }
    }
}
