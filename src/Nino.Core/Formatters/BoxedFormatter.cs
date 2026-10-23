using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>Non-generic entry point to <see cref="NinoFormatterCache{T}"/> for a runtime type.</summary>
    internal abstract class BoxedFormatter
    {
        private static readonly ConcurrentDictionary<Type, BoxedFormatter> Cache =
            NinoUnload.Cache<BoxedFormatter>(formatter => formatter.GetType());

        // A lambda rather than a method group: older compilers (Unity's) allocate a delegate per method group use.
        public static BoxedFormatter Get(Type type) => Cache.GetOrAdd(type, t => Create(t));

        private static BoxedFormatter Create(Type type)
        {
            // A registered formatter (generated, or built earlier) creates it: no generic type is closed at run time.
            var formatter = NinoFormatterCache.Get(type);
            if (formatter == null)
            {
                NinoRegistry.RegisterType(type);
                formatter = NinoFormatterCache.Get(type);
            }

            if (formatter != null)
                return formatter.CreateBoxed();
#if NET8_0_OR_GREATER
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                NinoThrow.NoGeneratedFormatter(type);
                return null;
            }
#endif
            return CreateDynamic(type);
        }

        // BoxedFormatter<T> resolves T's formatter on first use, as the generic APIs do.
        [RequiresDynamicCode(NinoFormatterResolver.DynamicCodeMessage)]
        private static BoxedFormatter CreateDynamic(Type type) =>
            (BoxedFormatter)Activator.CreateInstance(typeof(BoxedFormatter<>).MakeGenericType(type));

        /// <summary>Whether the type has a formatter (resolving one on first use).</summary>
        public abstract bool HasFormatter { get; }

        public abstract byte[] Serialize(object value);
        public abstract void Serialize(object value, IBufferWriter<byte> bufferWriter);
        public abstract void Serialize(object value, ref Writer writer);
        public abstract void Deserialize(ref object value, ref Reader reader);
    }

    internal sealed class BoxedFormatter<T> : BoxedFormatter
    {
        public override bool HasFormatter => NinoTopFormatter<T>.Formatter != null;

        public override byte[] Serialize(object value) => NinoSerializer.Serialize(Unbox(value));

        public override void Serialize(object value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(Unbox(value), bufferWriter);

        public override void Serialize(object value, ref Writer writer) =>
            NinoTopFormatter<T>.Get.Serialize(Unbox(value), ref writer);

        /// <summary>
        /// <paramref name="value"/> as a <typeparamref name="T"/>; null only for a type that can be null (a cast
        /// would unbox it to a value type with a NullReferenceException).
        /// </summary>
        private static T Unbox(object value)
        {
            if (value == null && default(T) != null)
                NinoThrow.NullBoxedValue(typeof(T), nameof(value));
            return (T)value;
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            T typed = value is T existing ? existing : default;
            NinoTopFormatter<T>.Get.Deserialize(ref typed, ref reader);
            value = typed;
        }
    }
}
