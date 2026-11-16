using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>Non-generic entry point to <see cref="NinoFormatterCache{T}"/> for a runtime type.</summary>
    internal abstract class BoxedFormatter
    {
        private static readonly ConcurrentDictionary<Type, BoxedFormatter> Cache =
            NinoUnload.Cache<BoxedFormatter>(formatter => formatter.GetType());

        // Cache the factory explicitly for older compilers, which allocate a delegate at each method-group use.
        private static readonly Func<Type, BoxedFormatter> Factory = Create;

        public static BoxedFormatter Get(Type type) => Cache.GetOrAdd(type, Factory);

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
        public abstract void Deserialize(ref Reader reader, ref object value);
    }

    internal sealed class BoxedFormatter<T> : BoxedFormatter
    {
        public override bool HasFormatter => NinoTopFormatter<T>.Exists;

        public override byte[] Serialize(object value) => NinoSerializer.Serialize(Unbox(value));

        public override void Serialize(object value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(Unbox(value), bufferWriter);

        public override void Serialize(object value, ref Writer writer) =>
            NinoTopFormatter<T>.Formatter.Serialize(Unbox(value), ref writer);

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

        public override void Deserialize(ref Reader reader, ref object value)
        {
            T typed = value is T existing ? existing : default;
            NinoTopFormatter<T>.Formatter.Deserialize(ref reader, ref typed);
            value = typed;
        }
    }
}
