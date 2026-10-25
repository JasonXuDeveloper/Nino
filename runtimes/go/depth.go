package nino

import (
	"encoding/binary"
	"strconv"
)

// Class bodies, regions and deep nesting.
//
// Class bodies are read and written by nested calls, so a recursive type would use stack in proportion to the
// depth of its data. As in .NET, the reader and the writer count the open class bodies: a body that would open
// below InlineDepth (128) open bodies is deferred. The writer queues the object and writes nothing for its body
// yet; the reader, which counts the same way, creates the instance, stores it and queues it. Queued bodies are
// read and written later from a shallow point, so the bytes are exactly .NET's and the depth of the calls stays
// bounded whatever the data:
//
//   - outside any region: after the top-level object, once every body around it has closed, in order;
//   - inside a region (a version-tolerant body, a keyed body or member): in a section at the end of the outermost
//     region, `{int32 key, int32 length, body}*` then the section's offset, the region's length with its top bit
//     set; the key is where the deferral happened, relative to the region's start.
//
// Types that .NET builds through their constructor or compares by value (records, init-only members...) are never
// deferred: past 128 they nest in place, and past MaxDepth (512) reading or writing fails.

// Class describes the body of a [NinoType] class (its members, without the presence byte or type id in front):
// generated code declares one per class and reads and writes the class through [ReadClass] and [WriteClass], which
// count the depth and defer the body when needed.
type Class[T any] struct {
	// Read reads the members into v.
	Read func(r *Reader, v *T)
	// Write writes the members of v.
	Write func(w *Writer, v *T)
	// Deferrable is false for types .NET never defers (built by their constructor, or compared by value): it must
	// match the .NET type for the bytes to match.
	Deferrable bool
}

// body is a Class with its type erased, for the queues of deferred bodies.
type body interface {
	readAny(r *Reader, v any)
	writeAny(w *Writer, v any)
}

func (c *Class[T]) readAny(r *Reader, v any) {
	if p, ok := v.(*T); ok && p != nil {
		c.Read(r, p)
	}
}

func (c *Class[T]) writeAny(w *Writer, v any) {
	if p, ok := v.(*T); ok && p != nil {
		c.Write(w, p)
	}
}

// ReadClass reads the body of a class into a new instance and returns it (never nil): in place, or, as deep as
// .NET defers it, later (the instance is returned now and filled when its body is read).
func ReadClass[T any](r *Reader, c *Class[T]) *T {
	v := new(T)
	d := r.depth
	if d >= InlineDepth {
		r.readDeep(v, c, c.Deferrable)
		return v
	}
	r.depth = d + 1
	c.Read(r, v)
	r.exitBody(d)
	return v
}

// ReadClassInto is [ReadClass] into an instance the caller created (a subtype's dispatch, a type whose instance
// must exist before its body is read).
func ReadClassInto[T any](r *Reader, v *T, c *Class[T]) {
	if v == nil {
		r.Fail(CodeInvalidValue, "nil instance")
		return
	}
	d := r.depth
	if d >= InlineDepth {
		r.readDeep(v, c, c.Deferrable)
		return
	}
	r.depth = d + 1
	c.Read(r, v)
	r.exitBody(d)
}

// WriteClass writes the body of v (not nil: the caller wrote its presence byte or type id), in place or, as deep as
// .NET defers it, later.
func WriteClass[T any](w *Writer, v *T, c *Class[T]) {
	if v == nil {
		w.Fail(CodeInvalidValue, "nil body")
		return
	}
	d := w.depth
	if d >= InlineDepth {
		w.writeDeep(v, c, c.Deferrable)
		return
	}
	w.depth = d + 1
	c.Write(w, v)
	w.exitBody(d)
}

// deferredItem is a body read or written later.
type deferredItem struct {
	value any
	body  body
	// Where the deferral happened: keys the entry when the item is in a region's section.
	pos int
	// Writer only, to find cycles.
	anchor any
	gen    int
}

type queue struct {
	items []deferredItem
	head  int
}

func (q *queue) empty() bool { return q.head == len(q.items) }

func (q *queue) add(item deferredItem) { q.items = append(q.items, item) }

func (q *queue) next() deferredItem {
	item := q.items[q.head]
	q.head++
	return item
}

// clear empties the queue, dropping its references; a large one is not kept.
func (q *queue) clear() {
	if cap(q.items) > 1<<16 {
		q.items = nil
	} else {
		clear(q.items)
		q.items = q.items[:0]
	}
	q.head = 0
}

type readContext struct {
	top, region queue
	topActive   bool
	// Open struct bodies counted by EnterStruct.
	structs int32
	// The objects of tracked types read so far, in order (refs.go).
	refs []any
}

func (c *readContext) reset() {
	c.top.clear()
	c.region.clear()
	c.topActive = false
	c.structs = 0
	if c.refs != nil {
		clear(c.refs)
		c.refs = c.refs[:0]
	}
}

func (r *Reader) context() *readContext {
	if r.ctx == nil {
		r.ctx = &readContext{}
	}
	return r.ctx
}

//go:noinline
func (r *Reader) readDeep(v any, b body, deferrable bool) {
	if deferrable {
		ctx := r.context()
		item := deferredItem{value: v, body: b, pos: r.Pos()}
		if r.regions == 0 {
			ctx.top.add(item)
		} else {
			ctx.region.add(item)
		}
		return
	}
	d := r.depth
	var structs int32
	if r.ctx != nil {
		structs = r.ctx.structs
	}
	if d+structs >= MaxDepth {
		r.failTooDeep()
		return
	}
	r.depth = d + 1
	b.readAny(r, v)
	r.exitBody(d)
}

// exitBody closes a class body opened at depth d; once every body is closed, it reads the bodies deferred outside
// regions.
func (r *Reader) exitBody(d int32) {
	r.depth = d
	if d == 0 && r.ctx != nil && !r.ctx.top.empty() {
		r.readTopDeferred()
	}
}

//go:noinline
func (r *Reader) readTopDeferred() {
	ctx := r.ctx
	if ctx.topActive {
		return
	}
	ctx.topActive = true
	for !ctx.top.empty() && r.err == nil {
		item := ctx.top.next()
		r.depth = 1
		item.body.readAny(r, item.value)
	}
	r.depth = 0
	ctx.top.clear()
	ctx.topActive = false
}

// EnterStruct opens the body of a struct that can hold itself (through a collection, a nullable or an interface),
// or of a collection that holds itself: no class body counts such nesting, so it is counted here with the open
// class bodies, up to MaxDepth. It returns false (and the reader has failed) past that. Call [Reader.ExitStruct]
// after the body.
func (r *Reader) EnterStruct() bool {
	ctx := r.context()
	ctx.structs++
	if ctx.structs+r.depth > MaxDepth {
		r.failTooDeep()
		return false
	}
	return true
}

// failTooDeep fails the reader with CodeTooDeep: MaxDepth bodies are open and the next cannot be deferred.
//
//go:noinline
func (r *Reader) failTooDeep() { r.Fail(CodeTooDeep, strconv.Itoa(MaxDepth)+" open bodies") }

// ExitStruct closes a body opened by [Reader.EnterStruct].
func (r *Reader) ExitStruct() {
	if r.ctx != nil {
		r.ctx.structs--
	}
}

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

type writeContext struct {
	top, region queue
	topActive   bool
	// The deferred object whose body is being written, for cycle detection.
	current, currentAnchor any
	currentGen             int
	// Open struct bodies counted by EnterStruct.
	structs int32
	// The number of each object of a tracked type written so far (refs.go).
	refs map[any]int32
}

func (c *writeContext) reset() {
	c.top.clear()
	c.region.clear()
	c.topActive = false
	c.current, c.currentAnchor, c.currentGen = nil, nil, 0
	c.structs = 0
	clear(c.refs)
}

func (w *Writer) context() *writeContext {
	if w.ctx == nil {
		w.ctx = &writeContext{}
	}
	return w.ctx
}

// EnterStruct is [Reader.EnterStruct] for the writer: it fails (CodeTooDeep) past MaxDepth open bodies, where a
// reader would fail too.
func (w *Writer) EnterStruct() bool {
	ctx := w.context()
	ctx.structs++
	if ctx.structs+w.depth > MaxDepth {
		w.failTooDeep()
		return false
	}
	return true
}

// failTooDeep is [Reader.failTooDeep] for the writer.
//
//go:noinline
func (w *Writer) failTooDeep() { w.Fail(CodeTooDeep, strconv.Itoa(MaxDepth)+" open bodies") }

// ExitStruct closes a body opened by [Writer.EnterStruct].
func (w *Writer) ExitStruct() {
	if w.ctx != nil {
		w.ctx.structs--
	}
}

// writeDeep defers a body nested too deep, or (if it cannot be) writes it in place up to MaxDepth.
//
//go:noinline
func (w *Writer) writeDeep(v any, b body, deferrable bool) {
	if deferrable {
		w.deferBody(v, b)
		return
	}
	d := w.depth
	var structs int32
	if w.ctx != nil {
		structs = w.ctx.structs
	}
	if d+structs >= MaxDepth {
		w.failTooDeep()
		return
	}
	w.depth = d + 1
	b.writeAny(w, v)
	w.exitBody(d)
}

// deferBody queues the body of v. It fails on a cycle: the deferred objects form chains (each deferred while
// writing the previous one), and an object that comes back in its own chain would be written forever. The check
// compares it with the chain's object at the last power-of-two length (Brent), as .NET does.
func (w *Writer) deferBody(v any, b body) {
	ctx := w.context()
	gen := ctx.currentGen
	anchor := ctx.currentAnchor
	if gen != 0 && gen&(gen-1) == 0 {
		anchor = ctx.current
	}
	if v == ctx.current || v == anchor {
		w.Fail(CodeCycle, "an object contains itself")
		return
	}
	item := deferredItem{value: v, body: b, pos: len(w.buf), anchor: anchor, gen: gen + 1}
	if w.regions == 0 {
		ctx.top.add(item)
	} else {
		ctx.region.add(item)
	}
}

func (w *Writer) writeDeferred(item deferredItem, depth int32) {
	ctx := w.ctx
	ctx.current, ctx.currentAnchor, ctx.currentGen = item.value, item.anchor, item.gen
	w.depth = depth
	item.body.writeAny(w, item.value)
}

func (w *Writer) exitBody(d int32) {
	w.depth = d
	if d == 0 && w.ctx != nil && !w.ctx.top.empty() {
		w.writeTopDeferred()
	}
}

//go:noinline
func (w *Writer) writeTopDeferred() {
	ctx := w.ctx
	if ctx.topActive {
		return
	}
	ctx.topActive = true
	for !ctx.top.empty() && w.err == nil {
		w.writeDeferred(ctx.top.next(), 1)
	}
	w.depth = 0
	ctx.top.clear()
	ctx.topActive = false
	ctx.current, ctx.currentAnchor, ctx.currentGen = nil, nil, 0
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

// KeyedEntry is a keyed member being read: its value's bytes.
type KeyedEntry struct {
	rest []byte
}

// BeginEntry reads a keyed entry's length (after its key, inside a keyed region) and limits the reader to the
// entry's value until [Reader.EndEntry]. A length that is negative or past the region fails.
func (r *Reader) BeginEntry() KeyedEntry {
	n := r.I32()
	if r.err != nil {
		return KeyedEntry{}
	}
	if n < 0 || int64(n) > int64(len(r.buf)) {
		r.badLength(int64(n))
		return KeyedEntry{}
	}
	b := r.buf
	r.buf = b[:n]
	return KeyedEntry{rest: b[n:]}
}

// EndEntry ends the entry e: the reader moves past it. A value that left bytes of its entry unread is invalid
// (writers make the length exactly the value's; .NET skips the rest).
func (r *Reader) EndEntry(e KeyedEntry) {
	if r.err != nil {
		return
	}
	if len(r.buf) != 0 {
		r.Fail(CodeInvalidData, "bytes left in a keyed member")
		return
	}
	r.buf = e.rest
}

// SkipEntry skips the value of a keyed entry whose key the reader does not know (after [Reader.BeginEntry]).
func (r *Reader) SkipEntry(e KeyedEntry) {
	if r.err != nil {
		return
	}
	r.buf = e.rest
}

// BeginEntry writes a keyed entry's key and reserves its length; [Writer.EndEntry] writes the length.
func (w *Writer) BeginEntry(key int32) int {
	w.I32(key)
	return w.ReserveInt32()
}

// EndEntry ends the entry whose length is at pos.
func (w *Writer) EndEntry(pos int) { w.patchLength(pos) }

// ReadSealed reads a sealed class: its presence byte, then (when present) its body. nil for null.
func ReadSealed[T any](r *Reader, c *Class[T]) *T {
	if !r.Present() {
		return nil
	}
	return ReadClass(r, c)
}

// WriteSealed writes a sealed class: its presence byte, then (unless v is nil) its body.
func WriteSealed[T any](w *Writer, v *T, c *Class[T]) {
	if v == nil {
		w.U8(0)
		return
	}
	w.U8(1)
	WriteClass(w, v, c)
}

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
