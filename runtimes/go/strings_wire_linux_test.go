package nino

import (
	"strings"
	"syscall"
	"testing"
	"unsafe"
)

// guardedPage maps three pages and returns the middle one: the pages around it cannot be read, so a kernel that reads
// a byte outside the text it was given there faults. The test is skipped where that cannot be set up.
func guardedPage(t *testing.T) []byte {
	t.Helper()
	page := syscall.Getpagesize()
	mem, err := syscall.Mmap(-1, 0, 3*page, syscall.PROT_READ|syscall.PROT_WRITE, syscall.MAP_ANON|syscall.MAP_PRIVATE)
	if err != nil {
		t.Skip("mmap:", err)
	}
	t.Cleanup(func() { syscall.Munmap(mem) })
	if syscall.Mprotect(mem[:page], syscall.PROT_NONE) != nil || syscall.Mprotect(mem[2*page:], syscall.PROT_NONE) != nil {
		t.Skip("mprotect")
	}
	return mem[page : 2*page]
}

// writeStringsBoth writes strs with each vector path and without, and fails when the bytes differ.
func writeStringsBoth(t *testing.T, strs []string) {
	t.Helper()
	defer resetWire()
	setWire(0)
	ref := NewWriter(0)
	WriteStrings(ref, strs)
	for _, tier := range wireTiers() {
		setWire(tier)
		w := NewWriter(0)
		WriteStrings(w, strs)
		if string(w.Bytes()) != string(ref.Bytes()) {
			t.Fatalf("%q (%s): WriteStrings wrote\n% X\nwant\n% X", strs[0], tierName(tier), w.Bytes(), ref.Bytes())
		}
	}
}

// TestWireStringPageEnd writes strings that end at the last byte before a page that cannot be read: the vector
// paths must read no byte past the text there.
func TestWireStringPageEnd(t *testing.T) {
	body := guardedPage(t)
	for _, c := range wireChars {
		text := strings.Repeat("a", 70) + c + strings.Repeat(c, 30)
		for n := 0; n <= len(text); n++ {
			at := len(body) - n
			copy(body[at:], text[len(text)-n:])
			s := unsafe.String(unsafe.SliceData(body[at:]), n)
			for pad := 0; pad < 3; pad++ {
				writeBoth(t, s, pad, 0)
			}
			writeStringsBoth(t, []string{s, s[:n/2], s})
		}
	}
}

// TestWireStringsPageStart writes lists of strings that start at the first byte after a page that cannot be read:
// the list kernels' path for short ASCII text reads from the byte before the text, and must not there.
func TestWireStringsPageStart(t *testing.T) {
	body := guardedPage(t)
	copy(body, strings.Repeat("abcdefgh", 8)+"é")
	for n := 1; n <= 66; n++ {
		s := unsafe.String(unsafe.SliceData(body), n)
		writeStringsBoth(t, []string{s, s, s[:n/2]})
	}
}

// TestReadVectorPageEnds converts text that starts at the first byte after a page that cannot be read, and text
// that ends at the last byte before one: the reader's vector path must read no byte outside the text.
func TestReadVectorPageEnds(t *testing.T) {
	if !hasRead {
		t.Skip("no vector path here")
	}
	body := guardedPage(t)
	for i := range body {
		body[i] = byte(i*7) | 0x80
	}
	body[1], body[len(body)-1] = 0xDC, 0xD8 // a low surrogate first, a high one last
	for n := 0; n <= 300; n++ {
		for _, src := range [][]byte{body[:n], body[len(body)-n:]} {
			readBoth(t, src)
		}
	}
}

// TestUtf8ScanPageEnds checks text that starts at the first byte after a page that cannot be read, and text that
// ends at the last byte before one: utf8Scan's vector path must read no byte outside the pages of the text.
func TestUtf8ScanPageEnds(t *testing.T) {
	if !hasRead {
		t.Skip("no vector path here")
	}
	body := guardedPage(t)
	for _, text := range []string{"a", "é", "字", "🎉", "\xED\xA0\x80", "\xC3"} {
		for i := 0; i < len(body); i += len(text) {
			copy(body[i:], text)
		}
		for n := 1; n <= 300; n++ {
			for _, src := range [][]byte{body[:n], body[len(body)-n:]} {
				want, wantOK := scanRef(src)
				got, ok := scanVec(src)
				if ok != wantOK || (ok && got != want) {
					t.Fatalf("scanUTF8(% x) = %d %v, want %d %v", src, got, ok, want, wantOK)
				}
			}
		}
	}
}
