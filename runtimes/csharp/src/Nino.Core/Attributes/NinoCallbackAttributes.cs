using System;

namespace Nino.Core
{
    /// <summary>
    /// Marks a method of a <c>[NinoType]</c> class or struct that Nino calls right before it writes a value of the
    /// type (every value: top level, members, collection elements, subtypes in a base slot). An instance method
    /// (static is NINO042), non-generic, taking no parameters and returning <c>void</c> (NINO038), public or internal
    /// (NINO039); one per type and callback (NINO041), base classes first. Generated code calls it directly.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#callbacks"/>
    /// </summary>
    /// <remarks>
    /// On a struct it runs on the copy being written: what it changes is written, not kept. A value written as a back
    /// reference (<c>trackReferences</c>) is not written again, and its callbacks are not called again. A struct Nino
    /// copies as raw memory calls no code (NINO044). An exception thrown by the method ends the call with that exception.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class NinoOnSerializingAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method of a <c>[NinoType]</c> class or struct that Nino calls right after it wrote a value of the type
    /// (its members, and the elements of a <c>[NinoType]</c> collection). The rules of
    /// <see cref="NinoOnSerializingAttribute"/> apply; not called when writing the value threw.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#callbacks"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class NinoOnSerializedAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method of a <c>[NinoType]</c> class or struct that Nino calls right before it reads the members of a
    /// value of the type. A static method runs first, without the instance (before it is created); an instance method
    /// runs on the instance the members are read into: the existing one when deserializing into a value, one taken from
    /// the pool of a <c>[NinoPooled]</c> type, or a new one, created first. A type Nino creates from the members it
    /// reads (a constructor taking them, init-only or readonly members) has no instance yet: only a static method can
    /// run (NINO043). Otherwise the rules of <see cref="NinoOnSerializingAttribute"/> apply.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#callbacks"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class NinoOnDeserializingAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method of a <c>[NinoType]</c> class or struct that Nino calls on the instance right after it read its
    /// members (and created it, when its constructor takes them). Objects it holds are read by then, except a body
    /// nested deeper than 128 classes, which is read once the outer bodies are, and an object of a cycle read by
    /// reference (<c>trackReferences</c>) whose own body is still being read. The rules of
    /// <see cref="NinoOnSerializingAttribute"/> apply; not called when reading the value threw.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#callbacks"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class NinoOnDeserializedAttribute : Attribute
    {
    }
}
