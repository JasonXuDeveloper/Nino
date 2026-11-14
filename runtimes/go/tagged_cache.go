package nino

import (
	"reflect"
	"sync"
	"sync/atomic"
)

// Struct tags (tagged.go): the cache of built codecs, one per reflect.Type and variant.

type codecKey struct {
	t       reflect.Type
	variant uint8
}

const (
	variantDefault uint8 = iota
	variantUTF8
	variantElementNullable // []*T written as single T? per element (seq(nullable(...)))
)

// unsafeAccess selects pointer access to the fields of numbers instead of reflect's Set and Int (the unsafe path;
// both give the same bytes, which the tests check). Each mode has its own cache.
var unsafeAccess atomic.Bool

func init() { unsafeAccess.Store(true) }

type planCache struct {
	mu     sync.Mutex
	codecs sync.Map // codecKey -> *codec
	errs   sync.Map // codecKey -> error
	plans  map[reflect.Type]*plan
}

var caches [2]planCache

func currentCache() (*planCache, bool) {
	if unsafeAccess.Load() {
		return &caches[1], true
	}
	return &caches[0], false
}

// codecOf returns the codec of t, building it (and every type it reaches) on first use.
func codecOf(t reflect.Type, variant uint8) (*codec, error) {
	cache, fast := currentCache()
	k := codecKey{t, variant}
	if c, ok := cache.codecs.Load(k); ok {
		return c.(*codec), nil
	}
	if err, ok := cache.errs.Load(k); ok {
		return nil, err.(error)
	}
	cache.mu.Lock()
	defer cache.mu.Unlock()
	if c, ok := cache.codecs.Load(k); ok {
		return c.(*codec), nil
	}
	if cache.plans == nil {
		cache.plans = map[reflect.Type]*plan{}
	}
	b := &builder{cache: cache, fast: fast, building: map[codecKey]*codec{}, plans: map[reflect.Type]*plan{}}
	c, err := b.codec(t, variant, nil)
	if err != nil {
		cache.errs.Store(k, err)
		return nil, err
	}
	for bk, bc := range b.building {
		cache.codecs.Store(bk, bc)
	}
	for pt, p := range b.plans {
		cache.plans[pt] = p
	}
	return c, nil
}
