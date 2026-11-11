package nino

import (
	"encoding/binary"
)

// Regions: the length-prefixed bodies of version-tolerant and keyed types and keyed members, with the section of
// deferred bodies an outermost region may end with (see depth.go for when a body is deferred).

// Region is a length-prefixed region being read: a version-tolerant body, a keyed body or a keyed member.
type Region struct {
	// The bytes after the region (its members and its section of deferred bodies).
	rest []byte
	// The position where the region's members end.
	end int
	// Whether a section of deferred bodies follows the members.
	sectioned bool
}

// BeginRegion reads a region's length and limits the reader to its members until [Reader.EndRegion]. Members a
// newer version appended are left unread and skipped by EndRegion; members an older version did not write are
// missing ([Reader.More] is false).
func (r *Reader) BeginRegion() Region {
	b := r.buf
	if len(b) < 4 {
		r.eof()
		return Region{}
	}
	length := int32(binary.LittleEndian.Uint32(b))
	b = b[4:]
	members, total := int(length), int(length)
	sectioned := false
	if length < 0 || int64(length) > int64(len(b)) {
		// A region followed by a section of deferred bodies (its length has the top bit set), or invalid data:
		// the members end at the offset the section's last 4 bytes give.
		total = int(length & maxInt32)
		if length >= 0 || total < 4 || total > len(b) {
			r.badLength(int64(length))
			return Region{}
		}
		m := int32(binary.LittleEndian.Uint32(b[total-4:]))
		if m < 0 || int(m) > total-4 || r.regions != 0 || !validSection(b[m:total-4], total-4) {
			r.Fail(CodeInvalidData, "invalid section of deferred bodies")
			return Region{}
		}
		members = int(m)
		sectioned = true
	}
	r.buf = b[:members]
	r.regions++
	return Region{rest: b[total:], end: r.Pos() + members, sectioned: sectioned}
}

// validSection reports whether section (after the members of a region whose members and section take limit
// bytes) is a sequence of entries {key, length, body} that fills it exactly, each key a position inside the region
// (where a body was deferred: in the members, or in an earlier entry's body). Only the outermost region of a value
// may hold one: a flag on an inner region is invalid.
//
//go:noinline
func validSection(section []byte, limit int) bool {
	for len(section) != 0 {
		if len(section) < 8 {
			return false
		}
		key := int32(binary.LittleEndian.Uint32(section))
		length := int32(binary.LittleEndian.Uint32(section[4:]))
		section = section[8:]
		if key < 0 || int(key) >= limit || length < 0 || int64(length) > int64(len(section)) {
			return false
		}
		section = section[length:]
	}
	return true
}

// EndRegion ends the region g that [Reader.BeginRegion] started: the reader moves past it. At the end of the
// outermost region, it reads the bodies deferred inside it from its section.
func (r *Reader) EndRegion(g Region) {
	if r.err != nil {
		return
	}
	r.regions--
	r.buf = g.rest
	if r.regions == 0 && r.ctx != nil && !r.ctx.region.empty() {
		if !g.sectioned {
			r.Fail(CodeInvalidData, "missing deferred bodies")
			return
		}
		r.endDeferredRegion(g.end)
	}
}

// endDeferredRegion reads the section after the outermost region (whose members end at regionEnd; the reader is
// past the section and its offset): each body deferred inside the region is matched, in order, with its entry by
// key; entries for objects this reader skipped (members it does not know) are skipped. Bodies deferred while
// reading the section are appended to the queue and matched with the entries after.
//
//go:noinline
func (r *Reader) endDeferredRegion(regionEnd int) {
	ctx := r.ctx
	pos := r.Pos()
	sectionEnd := pos - 4
	if sectionEnd <= regionEnd || pos > len(r.data) {
		r.Fail(CodeInvalidData, "missing deferred bodies")
		return
	}
	// Keys are relative to the start of the region's members.
	start := int32(regionEnd) - int32(binary.LittleEndian.Uint32(r.data[sectionEnd:pos]))
	section := r.data[regionEnd:sectionEnd]
	saved, depth := r.buf, r.depth
	entryDepth := deferredDepth(depth)
	for !ctx.region.empty() {
		item := ctx.region.next()
		for {
			if len(section) < 8 {
				r.buf = section
				r.eof()
				return
			}
			key := int32(binary.LittleEndian.Uint32(section))
			length := int32(binary.LittleEndian.Uint32(section[4:]))
			section = section[8:]
			if length < 0 || int64(length) > int64(len(section)) {
				r.buf = section
				r.badLength(int64(length))
				return
			}
			entry := section[:length]
			section = section[length:]
			if start+key != int32(item.pos) {
				continue
			}
			r.buf = entry
			r.depth = entryDepth
			r.regions = 1
			item.body.readAny(r, item.value)
			if r.err != nil {
				return
			}
			if len(r.buf) != 0 {
				r.Fail(CodeInvalidData, "bytes left in a deferred body")
				return
			}
			break
		}
	}
	r.buf = saved
	r.depth = depth
	r.regions = 0
	ctx.region.clear()
}

// BeginRegion starts a length-prefixed region (a version-tolerant body, a keyed body or member) and returns the
// position of its length, for [Writer.EndRegion].
func (w *Writer) BeginRegion() int {
	w.regions++
	return w.ReserveInt32()
}

// EndRegion ends the region started at pos: writes its length. At the end of the outermost region, it writes the
// bodies deferred inside it as a section after the members.
func (w *Writer) EndRegion(pos int) {
	w.regions--
	if w.regions == 0 && w.ctx != nil && !w.ctx.region.empty() {
		w.endOuterRegion(pos)
		return
	}
	w.patchLength(pos)
}

// patchLength writes at pos the number of bytes after the 4 there.
func (w *Writer) patchLength(pos int) {
	n := len(w.buf) - pos - 4
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "region")
		return
	}
	w.PatchInt32(pos, int32(n))
}

//go:noinline
func (w *Writer) endOuterRegion(lengthPos int) {
	ctx := w.ctx
	start := lengthPos + 4
	section := len(w.buf)
	depth := w.depth
	current, anchor, gen := ctx.current, ctx.currentAnchor, ctx.currentGen
	w.regions = 1
	for !ctx.region.empty() && w.err == nil {
		item := ctx.region.next()
		w.I32(int32(item.pos - start))
		length := w.ReserveInt32()
		w.writeDeferred(item, deferredDepth(depth))
		w.patchLength(length)
	}
	w.regions = 0
	w.depth = depth
	ctx.current, ctx.currentAnchor, ctx.currentGen = current, anchor, gen
	ctx.region.clear()
	w.I32(int32(section - start))
	n := len(w.buf) - start
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "region")
		return
	}
	w.PatchInt32(lengthPos, int32(n)|sectionFlag)
}

// KeyedMember is a keyed member being read: its value's bytes.
type KeyedMember struct {
	rest []byte
}

// BeginMember reads a keyed member's length (after its key, inside a keyed region) and limits the reader to the
// member's value until [Reader.EndMember]. A length that is negative or past the region fails.
func (r *Reader) BeginMember() KeyedMember {
	n := r.I32()
	if r.err != nil {
		return KeyedMember{}
	}
	if n < 0 || int64(n) > int64(len(r.buf)) {
		r.badLength(int64(n))
		return KeyedMember{}
	}
	b := r.buf
	r.buf = b[:n]
	return KeyedMember{rest: b[n:]}
}

// EndMember ends the member m: the reader moves past it. A value that left bytes of its member unread is invalid
// (writers make the length exactly the value's; .NET skips the rest).
func (r *Reader) EndMember(m KeyedMember) {
	if r.err != nil {
		return
	}
	if len(r.buf) != 0 {
		r.Fail(CodeInvalidData, "bytes left in a keyed member")
		return
	}
	r.buf = m.rest
}

// SkipMember skips the value of a keyed member whose key the reader does not know (after [Reader.BeginMember]).
func (r *Reader) SkipMember(m KeyedMember) {
	if r.err != nil {
		return
	}
	r.buf = m.rest
}

// BeginMember writes a keyed member's key and reserves its length; [Writer.EndMember] writes the length.
func (w *Writer) BeginMember(key int32) int {
	w.I32(key)
	return w.ReserveInt32()
}

// EndMember ends the member whose length is at pos.
func (w *Writer) EndMember(pos int) { w.patchLength(pos) }

// KeyedSeen records that the keyed member with index i (0 to 63) of the body being read was read, through seen
// (a mask the generated read function keeps). A member read twice (its key, or a former key of it, repeated) fails
// with CodeDuplicateKey, and KeyedSeen returns false: .NET keeps the last entry, which lets two readers that pick
// different entries see different values.
func (r *Reader) KeyedSeen(seen *uint64, i uint) bool {
	bit := uint64(1) << (i & 63)
	if *seen&bit != 0 {
		r.Fail(CodeDuplicateKey, "keyed member repeated")
		return false
	}
	*seen |= bit
	return true
}
