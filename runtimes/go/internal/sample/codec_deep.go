package sample

import nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"

// Recursive types: [nino.ReadClass] and [nino.WriteClass] count the open class bodies and defer those past 128 as
// .NET does; RecordChain, which .NET never defers, nests in place up to 512.

var (
	chainClass         nino.Class[Chain]
	tolerantChainClass nino.Class[TolerantChain]
	keyedChainClass    nino.Class[KeyedChain]
	treeNodeClass      nino.Class[TreeNode]
	redLinkClass       nino.Class[RedLink]
	blueLinkClass      nino.Class[BlueLink]
	recordChainClass   nino.Class[RecordChain]
	tolerantRootClass  nino.Class[TolerantRoot]
	treeHolderClass    nino.Class[TreeHolder]
)

func init() {
	chainClass = nino.Class[Chain]{Read: readChainBody, Write: writeChainBody, Deferrable: true}
	tolerantChainClass = nino.Class[TolerantChain]{Read: readTolerantChainBody, Write: writeTolerantChainBody, Deferrable: true}
	keyedChainClass = nino.Class[KeyedChain]{Read: readKeyedChainBody, Write: writeKeyedChainBody, Deferrable: true}
	treeNodeClass = nino.Class[TreeNode]{Read: readTreeNodeBody, Write: writeTreeNodeBody, Deferrable: true}
	redLinkClass = nino.Class[RedLink]{Read: readRedLinkBody, Write: writeRedLinkBody, Deferrable: true}
	blueLinkClass = nino.Class[BlueLink]{Read: readBlueLinkBody, Write: writeBlueLinkBody, Deferrable: true}
	recordChainClass = nino.Class[RecordChain]{Read: readRecordChainBody, Write: writeRecordChainBody, Deferrable: false}
	tolerantRootClass = nino.Class[TolerantRoot]{Read: readTolerantRootBody, Write: writeTolerantRootBody, Deferrable: true}
	treeHolderClass = nino.Class[TreeHolder]{Read: readTreeHolderBody, Write: writeTreeHolderBody, Deferrable: true}
}

func (*RedLink) NinoTypeID() int32  { return idRedLink }
func (*BlueLink) NinoTypeID() int32 { return idBlueLink }
func (*RedLink) link()              {}
func (*BlueLink) link()             {}

func ReadChain(r *nino.Reader) *Chain                 { return nino.ReadSealed(r, &chainClass) }
func WriteChain(w *nino.Writer, v *Chain)             { nino.WriteSealed(w, v, &chainClass) }
func ReadTolerantChain(r *nino.Reader) *TolerantChain { return nino.ReadSealed(r, &tolerantChainClass) }
func WriteTolerantChain(w *nino.Writer, v *TolerantChain) {
	nino.WriteSealed(w, v, &tolerantChainClass)
}
func ReadKeyedChain(r *nino.Reader) *KeyedChain         { return nino.ReadSealed(r, &keyedChainClass) }
func WriteKeyedChain(w *nino.Writer, v *KeyedChain)     { nino.WriteSealed(w, v, &keyedChainClass) }
func ReadTreeNode(r *nino.Reader) *TreeNode             { return nino.ReadSealed(r, &treeNodeClass) }
func WriteTreeNode(w *nino.Writer, v *TreeNode)         { nino.WriteSealed(w, v, &treeNodeClass) }
func ReadRecordChain(r *nino.Reader) *RecordChain       { return nino.ReadSealed(r, &recordChainClass) }
func WriteRecordChain(w *nino.Writer, v *RecordChain)   { nino.WriteSealed(w, v, &recordChainClass) }
func ReadTolerantRoot(r *nino.Reader) *TolerantRoot     { return nino.ReadSealed(r, &tolerantRootClass) }
func WriteTolerantRoot(w *nino.Writer, v *TolerantRoot) { nino.WriteSealed(w, v, &tolerantRootClass) }
func ReadTreeHolder(r *nino.Reader) *TreeHolder         { return nino.ReadSealed(r, &treeHolderClass) }
func WriteTreeHolder(w *nino.Writer, v *TreeHolder)     { nino.WriteSealed(w, v, &treeHolderClass) }

// ReadLink reads a slot declared as Link (abstract).
func ReadLink(r *nino.Reader) Link {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idRedLink:
		return nino.ReadClass(r, &redLinkClass)
	case idBlueLink:
		return nino.ReadClass(r, &blueLinkClass)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WriteLink(w *nino.Writer, v Link) {
	switch v := v.(type) {
	case nil:
		w.TypeID(nino.NullTypeID)
	case *RedLink:
		nino.WriteTyped(w, idRedLink, v, &redLinkClass)
	case *BlueLink:
		nino.WriteTyped(w, idBlueLink, v, &blueLinkClass)
	}
}

func readChainBody(r *nino.Reader, v *Chain) {
	v.Value = r.I32()
	v.Next = ReadChain(r)
}

func writeChainBody(w *nino.Writer, v *Chain) {
	w.I32(v.Value)
	WriteChain(w, v.Next)
}

func readTolerantChainBody(r *nino.Reader, v *TolerantChain) {
	g := r.BeginRegion()
	if r.More() {
		v.Value = r.I32()
	}
	if r.More() {
		v.Next = ReadTolerantChain(r)
	}
	r.EndRegion(g)
}

func writeTolerantChainBody(w *nino.Writer, v *TolerantChain) {
	g := w.BeginRegion()
	w.I32(v.Value)
	WriteTolerantChain(w, v.Next)
	w.EndRegion(g)
}

func readKeyedChainBody(r *nino.Reader, v *KeyedChain) {
	g := r.BeginRegion()
	var seen uint64
	for r.More() {
		key := r.I32()
		e := r.BeginEntry()
		switch key {
		case keyKeyedChainValue:
			if r.KeyedSeen(&seen, 0) {
				v.Value = r.I32()
			}
		case keyKeyedChainNext:
			if r.KeyedSeen(&seen, 1) {
				v.Next = ReadKeyedChain(r)
			}
		default:
			r.SkipEntry(e)
			continue
		}
		r.EndEntry(e)
	}
	r.EndRegion(g)
}

func writeKeyedChainBody(w *nino.Writer, v *KeyedChain) {
	g := w.BeginRegion()
	e := w.BeginEntry(keyKeyedChainValue)
	w.I32(v.Value)
	w.EndEntry(e)
	e = w.BeginEntry(keyKeyedChainNext)
	WriteKeyedChain(w, v.Next)
	w.EndEntry(e)
	w.EndRegion(g)
}

func readTreeNodeBody(r *nino.Reader, v *TreeNode) {
	v.Value = r.I32()
	v.Children = readTreeNodes(r)
}

func writeTreeNodeBody(w *nino.Writer, v *TreeNode) {
	w.I32(v.Value)
	writeTreeNodes(w, v.Children)
}

func readRedLinkBody(r *nino.Reader, v *RedLink) {
	v.Value = r.I32()
	v.Next = ReadLink(r)
}

func writeRedLinkBody(w *nino.Writer, v *RedLink) {
	w.I32(v.Value)
	WriteLink(w, v.Next)
}

func readBlueLinkBody(r *nino.Reader, v *BlueLink) {
	v.Value = r.I32()
	v.Next = ReadLink(r)
	v.Note = r.StringPtr()
}

func writeBlueLinkBody(w *nino.Writer, v *BlueLink) {
	w.I32(v.Value)
	WriteLink(w, v.Next)
	w.StringPtr(v.Note)
}

func readRecordChainBody(r *nino.Reader, v *RecordChain) {
	v.Value = r.I32()
	v.Next = ReadRecordChain(r)
}

func writeRecordChainBody(w *nino.Writer, v *RecordChain) {
	w.I32(v.Value)
	WriteRecordChain(w, v.Next)
}

func readTolerantRootBody(r *nino.Reader, v *TolerantRoot) {
	g := r.BeginRegion()
	if r.More() {
		v.Name = r.StringPtr()
	}
	if r.More() {
		v.Chain = ReadChain(r)
	}
	if r.More() {
		v.After = r.I32()
	}
	r.EndRegion(g)
}

func writeTolerantRootBody(w *nino.Writer, v *TolerantRoot) {
	g := w.BeginRegion()
	w.StringPtr(v.Name)
	WriteChain(w, v.Chain)
	w.I32(v.After)
	w.EndRegion(g)
}

// Collections of themselves: no class body counts their nesting, so each level is counted with EnterStruct, up to
// 512 (where .NET runs out of stack).

func readTree(r *nino.Reader) Tree {
	if !r.EnterStruct() {
		return nil
	}
	v := readTrees(r)
	r.ExitStruct()
	return v
}

func writeTree(w *nino.Writer, v Tree) {
	if !w.EnterStruct() {
		return
	}
	writeTrees(w, v)
	w.ExitStruct()
}

func readDictTree(r *nino.Reader) DictTree {
	if !r.EnterStruct() {
		return nil
	}
	v := readStringDictTreeEntries(r)
	r.ExitStruct()
	return v
}

func writeDictTree(w *nino.Writer, v DictTree) {
	if !w.EnterStruct() {
		return
	}
	writeStringDictTreeEntries(w, v)
	w.ExitStruct()
}

func readTreeHolderBody(r *nino.Reader, v *TreeHolder) {
	v.Tree = readTree(r)
	v.DictTree = readDictTree(r)
}

func writeTreeHolderBody(w *nino.Writer, v *TreeHolder) {
	writeTree(w, v.Tree)
	writeDictTree(w, v.DictTree)
}
