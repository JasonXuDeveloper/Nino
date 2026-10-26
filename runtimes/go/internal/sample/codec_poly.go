package sample

import nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"

// Polymorphic slots read a type id and switch over the ids of the types the declared type allows; any other id
// fails. Writes switch over the Go types.

var (
	dogClass       nino.Class[Dog]
	puppyClass     nino.Class[Puppy]
	catClass       nino.Class[Cat]
	boxInt32Class  nino.Class[BoxInt32]
	boxStringClass nino.Class[BoxString]
	boxPointsClass nino.Class[BoxPoints]
	boxVec2Class   nino.Class[BoxVec2]
	squareClass    nino.Class[Square]
	circleClass    nino.Class[Circle]
	plainClass     nino.Class[Plain]
	zooClass       nino.Class[Zoo]

	// objects is what a slot declared as object allows: every class here (an explicit allow-list, where .NET
	// accepts any type it can find).
	objects *nino.TypeSet
)

func init() {
	dogClass = nino.Class[Dog]{Read: readDogBody, Write: writeDogBody, Deferrable: true}
	puppyClass = nino.Class[Puppy]{Read: readPuppyBody, Write: writePuppyBody, Deferrable: true}
	catClass = nino.Class[Cat]{Read: readCatBody, Write: writeCatBody, Deferrable: true}
	boxInt32Class = nino.Class[BoxInt32]{Read: readBoxInt32Body, Write: writeBoxInt32Body, Deferrable: true}
	boxStringClass = nino.Class[BoxString]{Read: readBoxStringBody, Write: writeBoxStringBody, Deferrable: true}
	boxPointsClass = nino.Class[BoxPoints]{Read: readBoxPointsBody, Write: writeBoxPointsBody, Deferrable: true}
	boxVec2Class = nino.Class[BoxVec2]{Read: readBoxVec2Body, Write: writeBoxVec2Body, Deferrable: true}
	squareClass = nino.Class[Square]{Read: readSquareBody, Write: writeSquareBody, Deferrable: true}
	circleClass = nino.Class[Circle]{Read: readCircleBody, Write: writeCircleBody, Deferrable: true}
	plainClass = nino.Class[Plain]{Read: readPlainBody, Write: writePlainBody, Deferrable: true}
	zooClass = nino.Class[Zoo]{Read: readZooBody, Write: writeZooBody, Deferrable: true}

	objects = nino.NewTypeSet(
		nino.ClassType(idDog, &dogClass),
		nino.ClassType(idPuppy, &puppyClass),
		nino.ClassType(idCat, &catClass),
		nino.ClassType(idBoxInt32, &boxInt32Class),
		nino.ClassType(idBoxString, &boxStringClass),
		nino.ClassType(idBoxPoints, &boxPointsClass),
		nino.ClassType(idBoxVec2, &boxVec2Class),
		nino.ClassType(idSquare, &squareClass),
		nino.ClassType(idCircle, &circleClass),
		nino.ClassType(idPlain, &plainClass),
		nino.ClassType(idZoo, &zooClass),
		nino.ClassType(idPoint, &pointClass),
		nino.ClassType(idOuter, &outerClass),
		nino.ClassType(idIntegers, &integersClass),
	)
}

func (*Dog) NinoTypeID() int32       { return idDog }
func (*Puppy) NinoTypeID() int32     { return idPuppy }
func (*Cat) NinoTypeID() int32       { return idCat }
func (*BoxInt32) NinoTypeID() int32  { return idBoxInt32 }
func (*BoxString) NinoTypeID() int32 { return idBoxString }
func (*BoxPoints) NinoTypeID() int32 { return idBoxPoints }
func (*BoxVec2) NinoTypeID() int32   { return idBoxVec2 }
func (*Square) NinoTypeID() int32    { return idSquare }
func (*Circle) NinoTypeID() int32    { return idCircle }
func (*Plain) NinoTypeID() int32     { return idPlain }
func (*Zoo) NinoTypeID() int32       { return idZoo }
func (*Point) NinoTypeID() int32     { return idPoint }
func (*Outer) NinoTypeID() int32     { return idOuter }
func (*Integers) NinoTypeID() int32  { return idIntegers }

func (*Dog) animal()       {}
func (*Puppy) animal()     {}
func (*Cat) animal()       {}
func (*BoxInt32) animal()  {}
func (*BoxString) animal() {}
func (*BoxPoints) animal() {}
func (*BoxVec2) animal()   {}
func (*Dog) dog()          {}
func (*Puppy) dog()        {}
func (*Square) shape()     {}
func (*Circle) shape()     {}

// ReadAnimal reads a slot declared as Animal (abstract): any of its concrete subtypes.
func ReadAnimal(r *nino.Reader) Animal {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idDog:
		return nino.ReadClass(r, &dogClass)
	case idPuppy:
		return nino.ReadClass(r, &puppyClass)
	case idCat:
		return nino.ReadClass(r, &catClass)
	case idBoxInt32:
		return nino.ReadClass(r, &boxInt32Class)
	case idBoxString:
		return nino.ReadClass(r, &boxStringClass)
	case idBoxPoints:
		return nino.ReadClass(r, &boxPointsClass)
	case idBoxVec2:
		return nino.ReadClass(r, &boxVec2Class)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WriteAnimal(w *nino.Writer, v Animal) {
	switch v := v.(type) {
	case nil:
		w.TypeID(nino.NullTypeID)
	case *Dog:
		nino.WriteTyped(w, idDog, v, &dogClass)
	case *Puppy:
		nino.WriteTyped(w, idPuppy, v, &puppyClass)
	case *Cat:
		nino.WriteTyped(w, idCat, v, &catClass)
	case *BoxInt32:
		nino.WriteTyped(w, idBoxInt32, v, &boxInt32Class)
	case *BoxString:
		nino.WriteTyped(w, idBoxString, v, &boxStringClass)
	case *BoxPoints:
		nino.WriteTyped(w, idBoxPoints, v, &boxPointsClass)
	case *BoxVec2:
		nino.WriteTyped(w, idBoxVec2, v, &boxVec2Class)
	}
}

// ReadDogType reads a slot declared as Dog (not sealed): a Dog or a Puppy.
func ReadDogType(r *nino.Reader) DogType {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idDog:
		return nino.ReadClass(r, &dogClass)
	case idPuppy:
		return nino.ReadClass(r, &puppyClass)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WriteDogType(w *nino.Writer, v DogType) {
	switch v := v.(type) {
	case nil:
		w.TypeID(nino.NullTypeID)
	case *Dog:
		nino.WriteTyped(w, idDog, v, &dogClass)
	case *Puppy:
		nino.WriteTyped(w, idPuppy, v, &puppyClass)
	}
}

// ReadShape reads a slot declared as IShape.
func ReadShape(r *nino.Reader) Shape {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idSquare:
		return nino.ReadClass(r, &squareClass)
	case idCircle:
		return nino.ReadClass(r, &circleClass)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WriteShape(w *nino.Writer, v Shape) {
	switch v := v.(type) {
	case nil:
		w.TypeID(nino.NullTypeID)
	case *Square:
		nino.WriteTyped(w, idSquare, v, &squareClass)
	case *Circle:
		nino.WriteTyped(w, idCircle, v, &circleClass)
	}
}

// ReadPlain reads a slot declared as Plain: not sealed, so behind a type id, though it has no subtype.
func ReadPlain(r *nino.Reader) *Plain {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idPlain:
		return nino.ReadClass(r, &plainClass)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WritePlain(w *nino.Writer, v *Plain) { nino.WriteTyped(w, idPlain, v, &plainClass) }

// ReadObject reads a slot declared as object, restricted to the allow-list.
func ReadObject(r *nino.Reader) nino.Typed {
	v, _ := nino.ReadAny(r, objects).(nino.Typed)
	return v
}

func WriteObject(w *nino.Writer, v nino.Typed) { nino.WriteAny(w, v, objects) }

func ReadZoo(r *nino.Reader) *Zoo     { return nino.ReadSealed(r, &zooClass) }
func WriteZoo(w *nino.Writer, v *Zoo) { nino.WriteSealed(w, v, &zooClass) }

func readDogBody(r *nino.Reader, v *Dog) {
	v.Name = r.StringPtr()
	v.Bones = r.I32()
}

func writeDogBody(w *nino.Writer, v *Dog) {
	w.StringPtr(v.Name)
	w.I32(v.Bones)
}

func readPuppyBody(r *nino.Reader, v *Puppy) {
	v.Name = r.StringPtr()
	v.Bones = r.I32()
	v.Sleepy = r.Bool()
}

func writePuppyBody(w *nino.Writer, v *Puppy) {
	w.StringPtr(v.Name)
	w.I32(v.Bones)
	w.Bool(v.Sleepy)
}

func readCatBody(r *nino.Reader, v *Cat) {
	v.Name = r.StringPtr()
	v.Lives = r.F32()
}

func writeCatBody(w *nino.Writer, v *Cat) {
	w.StringPtr(v.Name)
	w.F32(v.Lives)
}

func readBoxInt32Body(r *nino.Reader, v *BoxInt32) {
	v.Name = r.StringPtr()
	v.Value = r.I32()
}

func writeBoxInt32Body(w *nino.Writer, v *BoxInt32) {
	w.StringPtr(v.Name)
	w.I32(v.Value)
}

func readBoxStringBody(r *nino.Reader, v *BoxString) {
	v.Name = r.StringPtr()
	v.Value = r.StringPtr()
}

func writeBoxStringBody(w *nino.Writer, v *BoxString) {
	w.StringPtr(v.Name)
	w.StringPtr(v.Value)
}

func readBoxPointsBody(r *nino.Reader, v *BoxPoints) {
	v.Name = r.StringPtr()
	v.Value = nino.ReadSlice(r, 1, ReadPoint)
}

func writeBoxPointsBody(w *nino.Writer, v *BoxPoints) {
	w.StringPtr(v.Name)
	nino.WriteSlice(w, v.Value, WritePoint)
}

func readBoxVec2Body(r *nino.Reader, v *BoxVec2) {
	v.Name = r.StringPtr()
	v.Value = readRaw(r, 8, decodeVec2)
}

func writeBoxVec2Body(w *nino.Writer, v *BoxVec2) {
	w.StringPtr(v.Name)
	writeRaw(w, v.Value, 8, encodeVec2)
}

func readSquareBody(r *nino.Reader, v *Square) {
	v.Area = r.F64()
	v.Side = r.F64()
}

func writeSquareBody(w *nino.Writer, v *Square) {
	w.F64(v.Area)
	w.F64(v.Side)
}

func readCircleBody(r *nino.Reader, v *Circle) {
	v.Area = r.F64()
	v.Radius = r.F64()
}

func writeCircleBody(w *nino.Writer, v *Circle) {
	w.F64(v.Area)
	w.F64(v.Radius)
}

func readPlainBody(r *nino.Reader, v *Plain) { v.Value = r.I32() }

func writePlainBody(w *nino.Writer, v *Plain) { w.I32(v.Value) }

func readZooBody(r *nino.Reader, v *Zoo) {
	v.Star = ReadAnimal(r)
	v.Dog = ReadDogType(r)
	v.Animals = nino.ReadSlice(r, 4, ReadAnimal)
	v.Cages = nino.ReadSlice(r, 4, ReadAnimal)
	v.Shape = ReadShape(r)
	v.Shapes = nino.ReadSlice(r, 4, ReadShape)
	v.Plain = ReadPlain(r)
	v.Anything = ReadObject(r)
	v.ByName = nino.ReadUniqueEntries(r, 5, keyString, ReadAnimal)
}

func writeZooBody(w *nino.Writer, v *Zoo) {
	WriteAnimal(w, v.Star)
	WriteDogType(w, v.Dog)
	nino.WriteSlice(w, v.Animals, WriteAnimal)
	nino.WriteSlice(w, v.Cages, WriteAnimal)
	WriteShape(w, v.Shape)
	nino.WriteSlice(w, v.Shapes, WriteShape)
	WritePlain(w, v.Plain)
	WriteObject(w, v.Anything)
	nino.WriteEntries(w, v.ByName, (*nino.Writer).String, WriteAnimal)
}
