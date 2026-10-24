using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Nino.Core;

// The formatters of the BCL types Nino supports, declared exactly like third-party ones: generated code and the
// runtime resolver both read this table. Strings, object, [NinoType]s and raw unmanaged values are built in.

// Arrays: raw elements are copied as one block.
[assembly: NinoFormatter(typeof(UnmanagedArrayFormatter<>), Priority = 2)]
[assembly: NinoFormatter(typeof(NullableArrayFormatter<>), Priority = 1)]
[assembly: NinoFormatter(typeof(ArrayFormatter<,>))]
[assembly: NinoFormatter(typeof(Array2DFormatter<,>))]
[assembly: NinoFormatter(typeof(Array3DFormatter<,>))]
[assembly: NinoFormatter(typeof(Array4DFormatter<,>))]
[assembly: NinoFormatter(typeof(ArraySegmentFormatter<,>))]

// Value types with their own format.
[assembly: NinoFormatter(typeof(IntPtrFormatter))]
[assembly: NinoFormatter(typeof(UIntPtrFormatter))]
[assembly: NinoFormatter(typeof(DateTimeOffsetFormatter))]
[assembly: NinoFormatter(typeof(NullableFormatter<,>))]
[assembly: NinoFormatter(typeof(KeyValuePairFormatter<,,,>))]
[assembly: NinoFormatter(typeof(BigIntegerFormatter))]
[assembly: NinoFormatter(typeof(LazyFormatter<,>))]
[assembly: NinoFormatter(typeof(MemoryFormatter<,>))]
[assembly: NinoFormatter(typeof(ReadOnlyMemoryFormatter<,>))]
[assembly: NinoFormatter(typeof(ReadOnlySequenceFormatter<,>))]

// Classes with their own format.
[assembly: NinoFormatter(typeof(UriFormatter))]
[assembly: NinoFormatter(typeof(VersionFormatter))]
[assembly: NinoFormatter(typeof(StringBuilderFormatter))]
[assembly: NinoFormatter(typeof(BitArrayFormatter))]
[assembly: NinoFormatter(typeof(CultureInfoFormatter))]
[assembly: NinoFormatter(typeof(TimeZoneInfoFormatter))]

// Collections of a specific definition.
[assembly: NinoFormatter(typeof(DictionaryFormatter<,,,>))]
[assembly: NinoFormatter(typeof(QueueFormatter<,>))]
[assembly: NinoFormatter(typeof(StackFormatter<,>))]
#if NET8_0_OR_GREATER
[assembly: NinoFormatter(typeof(PriorityQueueFormatter<,,,>))]
#endif
[assembly: NinoFormatter(typeof(BlockingCollectionFormatter<,>))]
[assembly: NinoFormatter(typeof(GroupingFormatter<,,,>))]
[assembly: NinoFormatter(typeof(LookupFormatter<,,,>))]

// Collection interfaces, read into List<T>, HashSet<T> or Dictionary<TKey, TValue> (read-only wrappers around them).
[assembly: NinoFormatter(typeof(InterfaceListFormatter<,,>), typeof(IEnumerable<>), typeof(ICollection<>),
    typeof(IList<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>), typeof(ReadOnlyCollection<>),
    typeof(ReadOnlyObservableCollection<>))]
[assembly: NinoFormatter(typeof(InterfaceSetFormatter<,,>), typeof(ISet<>)
#if NET8_0_OR_GREATER
    , typeof(IReadOnlySet<>)
#endif
)]
[assembly: NinoFormatter(typeof(InterfaceDictionaryFormatter<,,,,>), typeof(IDictionary<,>),
    typeof(IReadOnlyDictionary<,>), typeof(ReadOnlyDictionary<,>))]

// Any other collection, by what it derives from or implements (higher priority wins).
[assembly: NinoFormatter(typeof(UnmanagedListFormatter<,>), Priority = 60)]
[assembly: NinoFormatter(typeof(NullableListFormatter<,>), Priority = 50)]
[assembly: NinoFormatter(typeof(ListFormatter<,,>), Priority = 40)]
[assembly: NinoFormatter(typeof(HashSetFormatter<,,>), Priority = 30)]
[assembly: NinoFormatter(typeof(QueueFormatter<,,>), Priority = 25)]
[assembly: NinoFormatter(typeof(StackFormatter<,,>), Priority = 25)]
[assembly: NinoFormatter(typeof(DictionaryFormatter<,,,,>), Priority = 20)]
[assembly: NinoFormatter(typeof(DictionaryLikeFormatter<,,,,>), Priority = 10)]
[assembly: NinoFormatter(typeof(ProducerConsumerFormatter<,,>), Priority = 5)]
[assembly: NinoFormatter(typeof(CollectionFormatter<,,>))]

// Tuples (up to 7 elements; a value tuple of 8 or more holds the rest in its eighth).
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(ValueTupleFormatter<,,,,,,,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,,,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,,,,,,,,,>))]
[assembly: NinoFormatter(typeof(TupleFormatter<,,,,,,,,,,,,,>))]
