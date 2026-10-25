package nino

// The names before the Reader and Writer methods were made uniform: Go initialisms in capitals (UTF8 beside UTF16)
// and the 128-bit integers named by width as the other integers are (I128, U128 beside I64, U64). Code nino-gen
// wrote before then calls them; each inlines to the method it names.

// Utf8 is [Reader.UTF8].
//
// Deprecated: use [Reader.UTF8].
func (r *Reader) Utf8() string { return r.UTF8() }

// Utf8Ptr is [Reader.UTF8Ptr].
//
// Deprecated: use [Reader.UTF8Ptr].
func (r *Reader) Utf8Ptr() *string { return r.UTF8Ptr() }

// NullableUtf8 is [Reader.NullableUTF8].
//
// Deprecated: use [Reader.NullableUTF8].
func (r *Reader) NullableUtf8() (string, bool) { return r.NullableUTF8() }

// Int128 is [Reader.I128].
//
// Deprecated: use [Reader.I128].
func (r *Reader) Int128() Int128 { return r.I128() }

// UInt128 is [Reader.U128].
//
// Deprecated: use [Reader.U128].
func (r *Reader) UInt128() UInt128 { return r.U128() }

// Utf8 is [Writer.UTF8].
//
// Deprecated: use [Writer.UTF8].
func (w *Writer) Utf8(s string) { w.UTF8(s) }

// Utf8Ptr is [Writer.UTF8Ptr] (its body, not a call to it: a call costs one more than the inliner's budget).
//
// Deprecated: use [Writer.UTF8Ptr].
func (w *Writer) Utf8Ptr(s *string) {
	if s == nil {
		w.Null()
		return
	}
	w.UTF8(*s)
}

// NullableUtf8 is [Writer.NullableUTF8].
//
// Deprecated: use [Writer.NullableUTF8].
func (w *Writer) NullableUtf8(s string, ok bool) { w.NullableUTF8(s, ok) }

// Int128 is [Writer.I128].
//
// Deprecated: use [Writer.I128].
func (w *Writer) Int128(v Int128) { w.I128(v) }

// UInt128 is [Writer.U128].
//
// Deprecated: use [Writer.U128].
func (w *Writer) UInt128(v UInt128) { w.U128(v) }
