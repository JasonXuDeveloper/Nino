using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Back references and the depth of struct bodies; the fields and the cursor are in Writer.cs.
    public ref partial struct Writer
    {
        /// <summary>
        /// Ends reference tracking: later objects are not matched against the ones written so far. Called by
        /// the <see cref="NinoSerializer"/> APIs that create the writer; the table is reused by the next call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            var context = _context;
            if (context == null)
                return;
            _context = null;
            NinoWriteContext.Return(context);
        }

        /// <summary>
        /// Writes the reference header of an object of a type marked <c>trackReferences</c>: <c>0</c> for null,
        /// the 1-based index of an object already written by this writer, or <c>-1</c> for a new object, in which
        /// case it returns true and the caller writes the object (see <see cref="NinoWire"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool WriteReference(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.NullReference);
                return false;
            }

            var context = _context ??= NinoWriteContext.Rent();
            int index = (context.Refs ??= NinoReferenceWriteTable.Rent()).GetOrAdd(value);
            UnsafeWrite(index == 0 ? NinoWire.NewReference : index);
            return index == 0;
        }

        /// <summary>
        /// Generated code: opens the body of a struct that can hold itself (through a collection, a nullable, a
        /// <c>Lazy</c> or an interface), which no class body counts, and each level of a collection that holds itself
        /// (<see cref="NestingFormatter{T,TFormatter}"/>): with the class bodies, more than
        /// <see cref="NinoSettings.MaxDepth"/> levels, or a stack running low, throw
        /// <see cref="InvalidOperationException"/> (the object graph is nested too deep, or holds itself) instead of
        /// overflowing the stack. Call <see cref="ExitStruct"/> after the body. Out of line, as these bodies are.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnterStruct(Type type)
        {
            var context = _context ??= NinoWriteContext.Rent();
            int open = ++context.Structs;
            if (open + Depth > NinoLimits.MaxDepth || ((open & 15) == 0 && !NinoDepth.StackLeft()))
                NinoThrow.WriteTooDeep(type);
        }

        /// <summary>Generated code: closes a body opened by <see cref="EnterStruct"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExitStruct() => _context.Structs--;

        /// <summary>The struct bodies <see cref="EnterStruct"/> opened, which the class body depth adds to.</summary>
        internal int OpenStructs => _context?.Structs ?? 0;
    }
}
