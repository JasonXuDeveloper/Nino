using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Back references and the depth of struct bodies; the fields and the cursor are in Reader.cs.
    public ref partial struct Reader
    {
        /// <summary>
        /// Ends reference tracking for this reader. Called by the <see cref="NinoDeserializer"/> APIs that create
        /// the reader; the table is reused by the next call. Slices of this reader must not be used afterwards.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            var context = _context;
            if (context == null)
                return;
            _context = null;
            NinoReadContext.Return(context);
        }

        /// <summary>
        /// Reads what <see cref="Writer.WriteReference"/> wrote. Returns true when a new object follows: the
        /// caller reads it, calls <see cref="SetReference"/> as soon as the instance exists (so cycles resolve),
        /// and <see cref="EndReference"/> when done. Otherwise <paramref name="value"/> is now null or the
        /// object read earlier.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadReference<T>(ref T value, out int index) where T : class
        {
            int head = UnsafeRead<int>();
            index = 0;
            if (head == NinoWire.NewReference)
            {
                index = BeginReference();
                return true;
            }

            if (head == NinoWire.NullReference)
                NinoRecycler.Discard(ref value);
            else
                value = GetReference<T>(head);
            return false;
        }

        private int BeginReference()
        {
            var context = _context ??= NinoReadContext.Rent();
            return (context.Refs ??= NinoReferenceReadTable.Rent()).Begin();
        }

        private T GetReference<T>(int index) where T : class
        {
            var refs = _context?.Refs;
            if (refs == null)
                NinoThrow.InvalidReference(index);
            var obj = refs.Get(index);
            if (obj is T t)
                return t;
            NinoThrow.ReferenceTypeMismatch(typeof(T), obj.GetType());
            return null;
        }

        /// <summary>Records the instance of the object whose header was just read.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReference(object value)
        {
            NinoReferenceReadTable.SetPending(_context?.Refs, value);
        }

        /// <summary>Records the object read after <see cref="ReadReference{T}"/> returned true.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndReference(int index, object value) => _context.Refs.Set(index, value);

        /// <summary>
        /// Generated code: opens the body of a struct that can hold itself (through a collection, a nullable, a
        /// <c>Lazy</c> or an interface). No class body counts such nesting (<see cref="NinoDepth"/>), so it is counted
        /// here, with the open class bodies, up to <see cref="NinoSettings.MaxDepth"/>, and the stack is checked every 16
        /// levels: deeply nested data throws instead of overflowing the stack. Call <see cref="ExitStruct"/> after the
        /// body. Out of line, as these bodies are: the reader's address reaches a call there already. A body that
        /// throws leaves the count raised, in a context the failed call drops. Collections that hold themselves count
        /// their levels here too (<see cref="NestingFormatter{T,TFormatter}"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnterStruct(Type type)
        {
            var context = _context ??= NinoReadContext.Rent();
            int open = ++context.Structs;
            if (open + Depth > NinoLimits.MaxDepth || ((open & 15) == 0 && !NinoDepth.StackLeft()))
                NinoThrow.TooDeep(type);
        }

        /// <summary>Generated code: closes a body opened by <see cref="EnterStruct"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExitStruct() => _context.Structs--;

        /// <summary>The struct bodies <see cref="EnterStruct"/> opened, which the class body depth adds to.</summary>
        internal int OpenStructs => _context?.Structs ?? 0;
    }
}
