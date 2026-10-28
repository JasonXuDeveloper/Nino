package nino

import (
	"reflect"
	"sync"
	"unsafe"
)

// Prefetching while writing a large slice: its elements' objects (a slice of pointers) and the text and arrays
// their members point to (strings, slices, pointers) are each a separate cache miss when they are scattered in
// memory, so a slice longer than its type's threshold is written by writePrefetched, which asks for those lines a
// few elements ahead: at element i the objects of element i+2*prefetchAhead, and the members of element
// i+prefetchAhead (loaded by then). Slices of other elements (numbers, structs of them) are contiguous: the
// hardware prefetcher covers them, and they never take this path. Without a prefetch instruction (hasPrefetch
// false) the check folds away.

const (
	// prefetchAhead is how many elements ahead members are prefetched, and the batch size (prefetch8's eight).
	prefetchAhead = 8
	// prefetchFloor is the shortest slice that looks up its type's plan.
	prefetchFloor = 512
	// prefetchBudget is the bytes a slice's elements may take before prefetching pays: about what the caches
	// hold close to the core.
	prefetchBudget = 1 << 20
	// prefetchMemberCost is the estimated bytes behind each reference member (a string's text, a slice's array).
	prefetchMemberCost = 64
	// prefetchMembers is the most members prefetched per element.
	prefetchMembers = 4
)

// prefetchPlan is what writePrefetched prefetches for an element type.
type prefetchPlan struct {
	// pointer: the element is a pointer, whose target is prefetched first.
	pointer bool
	// members are the offsets (in the target of a pointer element, else in the element) of the reference members
	// whose targets are prefetched.
	members []uintptr
	// min is the shortest slice prefetched: prefetchBudget over the element's estimated bytes.
	min int
}

var prefetchPlans sync.Map // reflect.Type -> *prefetchPlan (nil plan: never prefetched)

// prefetchPlanOf returns the plan for elements of type t, or nil when they are not worth prefetching.
func prefetchPlanOf(t reflect.Type) *prefetchPlan {
	if p, ok := prefetchPlans.Load(t); ok {
		return p.(*prefetchPlan)
	}
	p := makePrefetchPlan(t)
	prefetchPlans.Store(t, p)
	return p
}

func makePrefetchPlan(t reflect.Type) *prefetchPlan {
	p := &prefetchPlan{}
	size := uintptr(0)
	switch t.Kind() {
	case reflect.Pointer:
		p.pointer = true
		t = t.Elem()
		size = t.Size()
	case reflect.Struct:
	default:
		return nil
	}
	if t.Kind() == reflect.Struct {
		for i := 0; i < t.NumField() && len(p.members) < prefetchMembers; i++ {
			switch f := t.Field(i); f.Type.Kind() {
			case reflect.String, reflect.Slice, reflect.Pointer, reflect.Map:
				// The first word of each is the address of what it refers to.
				p.members = append(p.members, f.Offset)
			}
		}
	}
	if !p.pointer && len(p.members) == 0 {
		return nil
	}
	p.min = max(prefetchBudget/int(size+uintptr(len(p.members))*prefetchMemberCost), prefetchFloor)
	return p
}

// writePrefetched writes the elements of v in batches of prefetchAhead, prefetching ahead, when their type has a
// plan and v is at least its threshold long; else it writes nothing and returns false. Out of line: WriteSlice
// keeps only its length check inline.
//
//go:noinline
func writePrefetched[T any](w *Writer, v []T, write func(*Writer, T)) bool {
	p := prefetchPlanOf(reflect.TypeFor[T]())
	if p == nil || len(v) < p.min {
		return false
	}
	var at [8]uintptr
	var size T
	stride := unsafe.Sizeof(size)
	first := unsafe.Pointer(unsafe.SliceData(v))
	n := len(v)
	for i := 0; i < n; i += prefetchAhead {
		if p.pointer {
			// The objects of the batch after next.
			for k, j := 0, i+2*prefetchAhead; k < 8; k, j = k+1, j+1 {
				at[k] = 0
				if j < n {
					at[k] = *(*uintptr)(unsafe.Add(first, uintptr(j)*stride))
				}
			}
			prefetch8(&at)
		}
		// The members of the next batch (their objects were prefetched a batch ago).
		k := 0
		for j := i + prefetchAhead; j < min(i+2*prefetchAhead, n); j++ {
			e := unsafe.Add(first, uintptr(j)*stride)
			if p.pointer {
				if e = *(*unsafe.Pointer)(e); e == nil {
					continue
				}
			}
			for _, off := range p.members {
				at[k] = *(*uintptr)(unsafe.Add(e, off))
				if k++; k == 8 {
					prefetch8(&at)
					k = 0
				}
			}
		}
		if k != 0 {
			clear(at[k:])
			prefetch8(&at)
		}
		for _, e := range v[i:min(i+prefetchAhead, n)] {
			write(w, e)
		}
	}
	return true
}
