package nino

import (
	"strings"
	"syscall"
	"testing"
	"unsafe"
)

// TestWireStringPageEnd writes strings that end at the last byte before a page that cannot be read: the vector
// paths must read no byte past the text there.
func TestWireStringPageEnd(t *testing.T) {
	page := syscall.Getpagesize()
	mem, err := syscall.Mmap(-1, 0, 2*page, syscall.PROT_READ|syscall.PROT_WRITE, syscall.MAP_ANON|syscall.MAP_PRIVATE)
	if err != nil {
		t.Skip("mmap:", err)
	}
	defer syscall.Munmap(mem)
	defer resetWire()
	if err := syscall.Mprotect(mem[page:], syscall.PROT_NONE); err != nil {
		t.Skip("mprotect:", err)
	}
	for _, c := range wireChars {
		text := strings.Repeat("a", 70) + c + strings.Repeat(c, 30)
		for n := 0; n <= len(text); n++ {
			at := page - n
			copy(mem[at:page], text[len(text)-n:])
			s := unsafe.String(unsafe.SliceData(mem[at:]), n)
			for pad := 0; pad < 3; pad++ {
				writeBoth(t, s, pad, 0)
			}
			strs := []string{s, s[:n/2], s}
			setWire(0)
			ref := NewWriter(0)
			WriteStrings(ref, strs)
			for _, tier := range wireTiers() {
				setWire(tier)
				w := NewWriter(0)
				WriteStrings(w, strs)
				if string(w.Bytes()) != string(ref.Bytes()) {
					t.Fatalf("%q (%s): WriteStrings wrote\n% X\nwant\n% X", s, tierName(tier), w.Bytes(), ref.Bytes())
				}
			}
			resetWire()
		}
	}
}

// TestWireStringsPageStart writes lists of strings that start at the first byte after a page that cannot be read:
// the list kernels' path for short ASCII text reads from the byte before the text, and must not there.
func TestWireStringsPageStart(t *testing.T) {
	page := syscall.Getpagesize()
	mem, err := syscall.Mmap(-1, 0, 2*page, syscall.PROT_READ|syscall.PROT_WRITE, syscall.MAP_ANON|syscall.MAP_PRIVATE)
	if err != nil {
		t.Skip("mmap:", err)
	}
	defer syscall.Munmap(mem)
	defer resetWire()
	copy(mem[page:], strings.Repeat("abcdefgh", 8)+"é")
	if err := syscall.Mprotect(mem[:page], syscall.PROT_NONE); err != nil {
		t.Skip("mprotect:", err)
	}
	for n := 1; n <= 66; n++ {
		s := unsafe.String(unsafe.SliceData(mem[page:]), n)
		strs := []string{s, s, s[:n/2]}
		setWire(0)
		ref := NewWriter(0)
		WriteStrings(ref, strs)
		for _, tier := range wireTiers() {
			setWire(tier)
			w := NewWriter(0)
			WriteStrings(w, strs)
			if string(w.Bytes()) != string(ref.Bytes()) {
				t.Fatalf("%q (%s): WriteStrings wrote\n% X\nwant\n% X", s, tierName(tier), w.Bytes(), ref.Bytes())
			}
		}
		resetWire()
	}
}

// TestReadVectorPageEnds converts text that starts at the first byte after a page that cannot be read, and text
// that ends at the last byte before one: the reader's vector path must read no byte outside the text.
func TestReadVectorPageEnds(t *testing.T) {
	if !hasRead {
		t.Skip("no vector path here")
	}
	page := syscall.Getpagesize()
	mem, err := syscall.Mmap(-1, 0, 3*page, syscall.PROT_READ|syscall.PROT_WRITE, syscall.MAP_ANON|syscall.MAP_PRIVATE)
	if err != nil {
		t.Skip("mmap:", err)
	}
	defer syscall.Munmap(mem)
	if syscall.Mprotect(mem[:page], syscall.PROT_NONE) != nil || syscall.Mprotect(mem[2*page:], syscall.PROT_NONE) != nil {
		t.Skip("mprotect")
	}
	body := mem[page : 2*page]
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
