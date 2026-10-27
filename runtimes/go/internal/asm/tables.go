// The tables both files share.

package main

// The checks of the UTF-8 validation (Keiser and Lemire, "Validating UTF-8 in less than one instruction per byte"),
// without the one for surrogates: the text is WTF-8, and a surrogate is one UTF-16 code unit like any other char.
const (
	tooShort     = 1 << 0
	tooLong      = 1 << 1
	overlong3    = 1 << 2
	tooLarge     = 1 << 3
	overlong2    = 1 << 5
	tooLarge1000 = 1 << 6
	overlong4    = 1 << 6
	twoConts     = 1 << 7
	carry        = tooShort | tooLong | twoConts
)

// validationTables are the three tables of the UTF-8 validation: the checks by the high nibble of the byte before,
// by its low nibble, and by the high nibble of the byte.
func validationTables() (t1, t2, t3 [16]byte) {
	for i := range t1 {
		switch {
		case i < 8:
			t1[i] = tooLong
		case i < 12:
			t1[i] = twoConts
		case i == 12:
			t1[i] = tooShort | overlong2
		case i == 13:
			t1[i] = tooShort
		case i == 14:
			t1[i] = tooShort | overlong3
		default:
			t1[i] = tooShort | tooLarge | tooLarge1000 | overlong4
		}
		switch {
		case i == 0:
			t2[i] = carry | overlong3 | overlong2 | overlong4
		case i == 1:
			t2[i] = carry | overlong2
		case i < 4:
			t2[i] = carry
		case i == 4:
			t2[i] = carry | tooLarge
		default:
			t2[i] = carry | tooLarge | tooLarge1000
		}
		switch {
		case i < 8 || i >= 12:
			t3[i] = tooShort
		case i == 8:
			t3[i] = tooLong | overlong2 | twoConts | overlong3 | tooLarge1000 | overlong4
		case i == 9:
			t3[i] = tooLong | overlong2 | twoConts | overlong3 | tooLarge
		default:
			t3[i] = tooLong | overlong2 | twoConts | tooLarge
		}
	}
	return t1, t2, t3
}

// surrogate is the check strict UTF-8 adds: ED followed by A0 to BF, a surrogate encoded as three bytes.
const surrogate = 1 << 4

// strictTables are validationTables with the check for surrogates: the tables of utf8Scan's kernels, which accept
// what utf8.Valid accepts.
func strictTables() (t1, t2, t3 [16]byte) {
	t1, t2, t3 = validationTables()
	t1[0xE] |= surrogate
	t2[0xD] |= surrogate
	t3[0xA] |= surrogate
	t3[0xB] |= surrogate
	return t1, t2, t3
}

// shiftTable is 0 to 15, then 16 bytes of 80: read from 16 minus the bytes left on, the shuffle that moves the last
// bytes of a block to its start, zeros after them.
func shiftTable() []byte {
	t := make([]byte, 32)
	for i := range t {
		t[i] = byte(i)
		if i >= 16 {
			t[i] = 0x80
		}
	}
	return t
}
