package nino

import (
	"bytes"
	"errors"
	"math/rand"
	"reflect"
	"testing"
	"unsafe"
)

// TestBlockSizes checks that each BlockValue takes as many bytes in memory as on the wire, on this host.
func TestBlockSizes(t *testing.T) {
	for _, c := range []struct {
		got  uintptr
		want uintptr
	}{
		{unsafe.Sizeof(DateTime(0)), 8}, {unsafe.Sizeof(DateOnly(0)), 4}, {unsafe.Sizeof(TimeOnly(0)), 8},
		{unsafe.Sizeof(Decimal{}), DecimalSize}, {unsafe.Sizeof(Guid{}), 16}, {unsafe.Sizeof(Int128{}), 16},
		{unsafe.Sizeof(UInt128{}), 16},
	} {
		if c.got != c.want {
			t.Fatalf("%d bytes, want %d", c.got, c.want)
		}
	}
}

// TestBlocks checks ReadBlock and WriteBlock against reading and writing the elements one by one: the same bytes,
// values, errors and error offsets, for every length to 24 with an invalid value (bits a reader rejects) at each
// position.
func TestBlocks(t *testing.T) {
	rnd := rand.New(rand.NewSource(1))
	u64 := func() uint64 { return rnd.Uint64() }
	blockCase(t, rnd, func() DateTime { return DateTime(u64() % (MaxTicks + 1)) },
		func() DateTime { return DateTime(MaxTicks + 1 + u64()%1000) }, (*Reader).DateTime, (*Writer).DateTime)
	blockCase(t, rnd, func() DateOnly { return DateOnly(u64() % uint64(MaxDateOnly+1)) },
		func() DateOnly { return -1 - DateOnly(u64()%5) }, (*Reader).DateOnly, (*Writer).DateOnly)
	blockCase(t, rnd, func() TimeOnly { return TimeOnly(u64() % uint64(MaxTimeOnly+1)) },
		func() TimeOnly { return MaxTimeOnly + 1 }, (*Reader).TimeOnly, (*Writer).TimeOnly)
	blockCase(t, rnd, func() Decimal { return Decimal{Flags: uint32(u64()%29) << 16, Hi: uint32(u64()), Lo: u64()} },
		func() Decimal { return Decimal{Flags: 29 << 16} }, (*Reader).Decimal, (*Writer).Decimal)
	blockCase(t, rnd, func() Guid {
		var g Guid
		rnd.Read(g[:])
		return g
	}, nil, (*Reader).Guid, (*Writer).Guid)
	blockCase(t, rnd, func() Int128 { return Int128{u64(), u64()} }, nil, (*Reader).I128, (*Writer).I128)
	blockCase(t, rnd, func() UInt128 { return UInt128{u64(), u64()} }, nil, (*Reader).U128, (*Writer).U128)
}

func blockCase[T BlockValue](t *testing.T, rnd *rand.Rand, valid, invalid func() T, read func(*Reader) T, write func(*Writer, T)) {
	t.Helper()
	var zero T
	size := int(unsafe.Sizeof(zero))
	for n := 0; n <= 24; n++ {
		v := make([]T, n)
		for i := range v {
			v[i] = valid()
		}
		want := encode(t, func(w *Writer) { WriteSlice(w, v, write) })
		if got := encode(t, func(w *Writer) { WriteBlock(w, v) }); !bytes.Equal(got, want) {
			t.Fatalf("%T n=%d: wrote %x, want %x", zero, n, got, want)
		}
		r := NewReader(want)
		if back := ReadBlock[T](r); r.Err() != nil || !reflect.DeepEqual(back, v) {
			t.Fatalf("%T n=%d: read %v (%v)", zero, n, back, r.Err())
		}
		if invalid == nil {
			continue
		}
		for i := 0; i < n; i++ {
			bad := append([]T(nil), v...)
			bad[i] = invalid()
			// The writer fails where writing one by one fails, after the same bytes.
			w1, w2 := NewWriter(0), NewWriter(0)
			WriteSlice(w1, bad, write)
			WriteBlock(w2, bad)
			if !sameError(w1.Err(), w2.Err()) || !bytes.Equal(w1.Bytes()[:w1.err.Offset], w2.Bytes()[:w2.err.Offset]) {
				t.Fatalf("%T n=%d bad=%d: wrote %v, want %v", zero, n, i, w2.Err(), w1.Err())
			}
			// Its bytes (written as a block of memory) fail to read where reading one by one fails.
			data := append([]byte(nil), want...)
			copy(data[4+i*size:], unsafe.Slice((*byte)(unsafe.Pointer(&bad[i])), size))
			if !littleEndian {
				encodeBlock(data[4+i*size:], bad[i:i+1])
			}
			r1, r2 := NewReader(data), NewReader(data)
			ReadSlice(r1, size, read)
			if got := ReadBlock[T](r2); got != nil || !sameError(r1.Err(), r2.Err()) {
				t.Fatalf("%T n=%d bad=%d: read %v, want %v", zero, n, i, r2.Err(), r1.Err())
			}
		}
	}
	expectBytes(t, encode(t, func(w *Writer) { WriteBlock[T](w, nil) }), "FFFFFFFF")
	if v := ReadBlock[T](NewReader(hx("FFFFFFFF"))); v != nil {
		t.Fatal(v)
	}
	expectCode(t, hx("02000000 00"), CodeInvalidLength, func(r *Reader) { ReadBlock[T](r) })
}

// sameError reports whether a and b are both nil or both *Error with the same code and offset.
func sameError(a, b error) bool {
	var x, y *Error
	if !errors.As(a, &x) || !errors.As(b, &y) {
		return a == nil && b == nil
	}
	return x.Code == y.Code && x.Offset == y.Offset && x.Detail == y.Detail
}
