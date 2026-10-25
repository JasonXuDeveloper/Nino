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
