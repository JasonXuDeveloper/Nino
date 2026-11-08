using System;
using Nino.Core;

// Positional types with serialization callbacks, each next to a twin without them: the same bytes, while the twin
// takes a path that runs no code of the type (a block or WriteFixed for collections of a struct of fixed size, the
// raw body of a sealed class, a body inlined wherever it is used). The callbacks keep the type off those paths.
namespace Nino.UnitTests.Subset.Callbacks
{
    /// <summary>The calls of the callbacks below, per thread (test classes run side by side).</summary>
    public static class CallbackCounts
    {
        [ThreadStatic] public static int Serializing, Serialized, Deserializing, Deserialized;

        public static void Reset() => Serializing = Serialized = Deserializing = Deserialized = 0;
    }

    // Of fixed size, and a block in collections, without its callbacks: the tuple keeps it from being raw.
    [NinoType]
    public struct CallbackBlock
    {
        public int A;
        public (int, long) T;

        // Not serialized: set by the read's callback, on the value in its slot.
        [NinoIgnore] public int Doubled;

        [NinoOnSerializing]
        internal void Serializing() => CallbackCounts.Serializing++;

        [NinoOnSerialized]
        internal void Serialized() => CallbackCounts.Serialized++;

        [NinoOnDeserializing]
        internal static void Deserializing() => CallbackCounts.Deserializing++;

        [NinoOnDeserialized]
        internal void Deserialized()
        {
            CallbackCounts.Deserialized++;
            Doubled = A * 2;
        }
    }

    [NinoType]
    public struct PlainBlock
    {
        public int A;
        public (int, long) T;
    }

    // A raw body (written with its presence byte in one reservation) without its callbacks.
    [NinoType]
    public sealed class CallbackRawBody
    {
        public int A;
        public long B;

        [NinoOnSerializing]
        internal void Serializing() => CallbackCounts.Serializing++;

        [NinoOnDeserialized]
        internal void Deserialized() => CallbackCounts.Deserialized++;
    }

    [NinoType]
    public sealed class PlainRawBody
    {
        public int A;
        public long B;
    }

    // A body inlined wherever it is used, and into the top-level calls through its __Top twin.
    [NinoType]
    public sealed class CallbackInline
    {
        public int A;
        public string Name;

        [NinoOnSerialized]
        internal void Serialized() => CallbackCounts.Serialized++;

        [NinoOnDeserializing]
        internal void Deserializing() => CallbackCounts.Deserializing++;

        [NinoOnDeserialized]
        internal void Deserialized() => CallbackCounts.Deserialized++;
    }

    [NinoType]
    public sealed class PlainInline
    {
        public int A;
        public string Name;
    }
}
