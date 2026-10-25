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
					t.Fatalf("%q (path %d): WriteStrings wrote\n% X\nwant\n% X", s, tier, w.Bytes(), ref.Bytes())
				}
			}
			resetWire()
		}
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
			units := len(src) / 2
			want := make([]byte, 3*units)
			want = want[:utf16UTF8(want, src[:2*units])]
			got := make([]byte, 3*units+64)
			got = got[:readUTF16(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(src)), units)]
			if string(got) != string(want) {
				t.Fatalf("UTF-16 of %d units: wrote\n% X\nwant\n% X", units, got, want)
			}
			want = make([]byte, 2*len(src))
			want = want[:latin1UTF8(want, src)]
			got = make([]byte, 2*len(src)+64)
			got = got[:readLatin1(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(src)), len(src))]
			if string(got) != string(want) {
				t.Fatalf("Latin-1 of %d bytes: wrote\n% X\nwant\n% X", len(src), got, want)
			}
		}
	}
}
