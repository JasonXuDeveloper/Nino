package nino

// Constants of the wire format (Nino.Core's NinoWire and NinoDepth). Every multi-byte value is little-endian and
// unaligned; there is no header, so the declared type decides how the bytes are read.
const (
	// NullLength is the length of a null array or collection (a null string is the header 0).
	NullLength = -1
	// NullTypeID is the type id of a null polymorphic value; never a real type id.
	NullTypeID = 0

	// InlineDepth is the number of open class bodies after which the next one is deferred.
	InlineDepth = 128
	// MaxDepth is the number of open class bodies after which reading or writing fails, for types that cannot
	// be deferred.
	MaxDepth = 512
	// sectionFlag is set in the length of a region followed by a section of deferred bodies.
	sectionFlag = -1 << 31

	// amplification is the most memory a collection read allocates ahead of its elements, per byte of data left
	// (NinoElements.Amplification); presizeFloor what it may allocate however few bytes are left.
	amplification = 16
	presizeFloor  = 16 << 10
	// hashSlot is the memory a map takes per entry beyond its key and value, for the same bound.
	hashSlot = 12
	// maxArrayLength is the longest dimension of a multi-dimensional array (.NET's Array.MaxLength).
	maxArrayLength = 0x7FFFFFC7
	// maxInt32 is the longest string or collection the format can hold.
	maxInt32 = 1<<31 - 1
)

// deferredDepth is the depth a body deferred inside a region is read and written at, from its section, when the
// region ended at depth: one body deeper, but never as deep as InlineDepth.
func deferredDepth(depth int32) int32 {
	return min(depth, InlineDepth-1) + 1
}
