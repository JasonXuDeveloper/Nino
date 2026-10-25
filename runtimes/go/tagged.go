package nino

import (
	"reflect"
)

// Struct tags: Go structs mapped to .NET [NinoType]s without generated code (see the README, "Struct tags").
//
// Each Go type gets a codec plan the first time it is used: its fields, their order, keys and encodings, checked
// against the type's NinoLayout when it declares one. Plans are built once (under a lock), cached by reflect.Type,
// and immutable after; the steady-state cost is walking the plan. Every read goes through the same Reader as
// generated code, so the bounds, the length checks before allocation, the depth counting and the deferral are the
// same, and a plan that cannot express a type fails when it is built, with a *TagError naming the field.
//
// The parts: tagged_codec.go (the codecs and how a plan walks a body), tagged_cache.go (built codecs by type),
// tagged_build.go (the builder, one codec per Go type) and tagged_plan.go (struct plans: tags, field order, layout).

// TagError reports a Go type that struct tags cannot map: a bad tag, a field type the wire format has no encoding
// for, or a mismatch with the type's layout. It is a programming error, returned before any byte is read or
// written.
type TagError struct {
	Type   reflect.Type
	Field  string // the Go field (a path for promoted fields); empty for the type itself
	Detail string
}

func (e *TagError) Error() string {
	name := "<nil>"
	if e.Type != nil {
		name = e.Type.String()
	}
	if e.Field != "" {
		return "nino: " + name + "." + e.Field + ": " + e.Detail
	}
	return "nino: " + name + ": " + e.Detail
}

// The interfaces a Go type implements to declare its .NET shape.
type (
	// Layouted is implemented by a Go type that declares its .NET layout: the string .NET publishes for the type
	// (NinoTypeLayout), or any part of it ("format=Keyed"). See the README.
	Layouted interface{ NinoLayout() string }
)
