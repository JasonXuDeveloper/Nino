// Package nino reads and writes Nino's binary format (v4), the bytes the .NET serializer Nino writes, as specified
// in docs/spec/wire-format.md of the Nino repository.
//
// The package is the runtime that generated code calls: a [Reader] and a [Writer] for every primitive, string
// encoding and BCL value the format defines, lengths and regions, class bodies with the deferral of deeply nested
// ones ([Class], [ReadClass], [WriteClass]) and polymorphic values restricted to an allowed set of types
// ([TypeSet], [ReadAny]). The codecs of the types themselves are generated (or written by hand in the same style);
// [Serialize] and [Deserialize] run one with a pooled writer or reader.
//
// # Guarantees
//
//   - Reading never panics, on any input, and never reads outside the data. Every length is checked against the
//     bytes left before anything is allocated, and collections whose elements take far more memory than their
//     bytes start smaller and grow as they are read, so the memory a read allocates is bounded by a multiple of
//     the data's size.
//   - Nesting is bounded: class bodies nested deeper than 128 are deferred exactly as .NET defers them (the bytes
//     are the same), so the depth of the calls stays bounded whatever the data; bodies that cannot be deferred
//     fail past 512.
//   - A polymorphic value is read only as one of the types the caller allows there.
//   - There is one error type, [*Error]; a failed [Deserialize] returns no part of a value.
//   - The writer appends to a buffer that grows by doubling; reused through [Writer.Reset] (or the pool behind
//     [Serialize] and [Append]), writing a value allocates nothing.
//
// The package has no dependencies beyond the standard library and uses no reflection.
package nino
