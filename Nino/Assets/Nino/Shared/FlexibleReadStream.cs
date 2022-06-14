using System;
using System.IO;

namespace Nino.Shared
{
    /// <summary>
    /// Can change the buffer in anytime, however this stream is not writable
    /// </summary>
    public sealed class FlexibleReadStream : Stream
    {
        private byte[] internalBuffer; // Either allocated internally or externally.
        private int origin; // For user-provided arrays, start at this origin
        private int position; // read/write head.
        private int length; // Number of bytes within the memory stream
        private int capacity; // length of usable portion of buffer for stream

        // Note that _capacity == _buffer.Length for non-user-provided byte[]'s

        private bool expandable; // User-provided buffers aren't expandable.
        private readonly bool exposable; // Whether the array can be returned to the user.
        private bool isOpen; // Is this stream open or closed?

        private readonly uint maxLength = 2147483648;

        private const int MemStreamMaxLength = Int32.MaxValue;

        public void ChangeBuffer(byte[] data)
        {
            internalBuffer = data;
            position = 0;
            origin = 0;
            length = data.Length;
        }

        public FlexibleReadStream(byte[] internalBuffer)
        {
            this.internalBuffer = internalBuffer ?? throw new ArgumentNullException(nameof(internalBuffer), "buffer == null");
            length = capacity = internalBuffer.Length;
            exposable = false;
            origin = 0;
            isOpen = true;
        }

        public FlexibleReadStream(byte[] internalBuffer,int index, int count)
            : this(internalBuffer, index, count, false)
        {
        }

        public FlexibleReadStream(byte[] internalBuffer, int index, int count, bool publiclyVisible)
        {
            if (internalBuffer == null)
                throw new ArgumentNullException(nameof(internalBuffer), "buffer == null");
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "index < 0");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count < 0");
            if (internalBuffer.Length - index < count)
                throw new ArgumentException("invalid length of buffer");

            this.internalBuffer = internalBuffer;
            origin = position = index;
            length = capacity = index + count;
            exposable = publiclyVisible; // Can TryGetBuffer/GetBuffer return the array?
            expandable = false;
            isOpen = true;
        }

        public override bool CanRead => isOpen;

        public override bool CanSeek => isOpen;

        public override bool CanWrite => false;

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    isOpen = false;
                    expandable = false;
                    // Don't set buffer to null - allow TryGetBuffer, GetBuffer & ToArray to work.
                }
            }
            finally
            {
                // Call base.Close() to cleanup async IO resources
                base.Dispose(disposing);
            }
        }

        // returns a bool saying whether we allocated a new array.
        private bool EnsureCapacity(int value)
        {
            // Check for overflow
            if (value < 0)
                throw new IOException("Stream too long, value < capacity of stream is invalid");
            if (value > capacity)
            {
                int newCapacity = value;
                if (newCapacity < 256)
                    newCapacity = 256;
                // We are ok with this overflowing since the next statement will deal
                // with the cases where _capacity*2 overflows.
                if (newCapacity < capacity * 2)
                    newCapacity = capacity * 2;
                // We want to expand the array up to Array.MaxArrayLengthOneDimensional
                // And we want to give the user the value that they asked for
                if ((uint)(capacity * 2) > maxLength)
                    newCapacity = value < maxLength ? value : (int)(maxLength / 2);

                Capacity = newCapacity;
                return true;
            }

            return false;
        }

        public override void Flush()
        {
        }


        public byte[] GetBuffer()
        {
            if (!exposable)
                throw new UnauthorizedAccessException("UnauthorizedAccess to get member buffer");
            return internalBuffer;
        }

        public bool TryGetBuffer(out ArraySegment<byte> buffer)
        {
            if (!exposable)
            {
                buffer = default(ArraySegment<byte>);
                return false;
            }

            buffer = new ArraySegment<byte>(this.internalBuffer, offset: origin, count: (length - origin));
            return true;
        }

        // Gets & sets the capacity (number of bytes allocated) for this stream.
        // The capacity cannot be set to a value less than the current length
        // of the stream.
        // 
        public int Capacity
        {
            get
            {
                if (!isOpen) Logger.E("stream is closed");
                return capacity - origin;
            }
            set
            {
                // Only update the capacity if the MS is expandable and the value is different than the current capacity.
                // Special behavior if the MS isn't expandable: we don't throw if value is the same as the current capacity
                if (value < Length)
                    throw new ArgumentOutOfRangeException(nameof(value), "value < capcacity is invalid");

                if (!isOpen) Logger.E("stream is closed");
                if (!expandable && (value != Capacity)) Logger.E("RewritableStream is not expandable");

                // RewritableStream has this invariant: _origin > 0 => !expandable (see ctors)
                if (expandable && value != capacity)
                {
                    if (value > 0)
                    {
                        byte[] newBuffer = new byte[value];
                        if (length > 0) Buffer.BlockCopy(internalBuffer, 0, newBuffer, 0, length);
                        internalBuffer = newBuffer;
                    }
                    else
                    {
                        internalBuffer = null;
                    }

                    capacity = value;
                }
            }
        }

        public override long Length
        {
            get
            {
                if (!isOpen) Logger.E("stream is closed");
                return length - origin;
            }
        }

        public override long Position
        {
            get
            {
                if (!isOpen) Logger.E("stream is closed");
                return position - origin;
            }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "value < 0 is invalid");
                if (!isOpen) Logger.E("stream is closed");

                if (value > MemStreamMaxLength)
                    throw new ArgumentOutOfRangeException(nameof(value), "value > stream length is invalid");
                position = origin + (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer), "buffer == null");
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset < 0");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count < 0");
            if (buffer.Length - offset < count)
                throw new ArgumentException("invalid buffer length");

            if (!isOpen) Logger.E("stream is closed");

            int n = length - position;
            if (n > count) n = count;
            if (n <= 0)
                return 0;

            if (n <= 8)
            {
                int byteCount = n;
                while (--byteCount >= 0)
                    buffer[offset + byteCount] = this.internalBuffer[position + byteCount];
            }
            else
                Buffer.BlockCopy(this.internalBuffer, position, buffer, offset, n);

            position += n;

            return n;
        }

        public override int ReadByte()
        {
            if (!isOpen) Logger.E("stream is closed");

            if (position >= length) return -1;

            return internalBuffer[position++];
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            if (!isOpen) Logger.E("stream is closed");

            if (offset > MemStreamMaxLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset > stream length is invalid");
            switch (loc)
            {
                case SeekOrigin.Begin:
                {
                    int tempPosition = unchecked(origin + (int)offset);
                    if (offset < 0 || tempPosition < origin)
                        throw new IOException("offset < 0 from the beginning of stream is invalid");
                    position = tempPosition;
                    break;
                }
                case SeekOrigin.Current:
                {
                    int tempPosition = unchecked(position + (int)offset);
                    if (unchecked(position + offset) < origin || tempPosition < origin)
                        throw new IOException("offset is before the stream which is invalid");
                    position = tempPosition;
                    break;
                }
                case SeekOrigin.End:
                {
                    int tempPosition = unchecked(length + (int)offset);
                    if (unchecked(length + offset) < origin || tempPosition < origin)
                        throw new IOException("offset is before the stream which is invalid");
                    position = tempPosition;
                    break;
                }
                default:
                    throw new ArgumentException("invalid seek origin");
            }

            return position;
        }

        // Sets the length of the stream to a given value.  The new
        // value must be nonnegative and less than the space remaining in
        // the array, Int32.MaxValue - origin
        // Origin is 0 in all cases other than a RewritableStream created on
        // top of an existing array and a specific starting offset was passed 
        // into the RewritableStream constructor.  The upper bounds prevents any 
        // situations where a stream may be created on top of an array then 
        // the stream is made longer than the maximum possible length of the 
        // array (Int32.MaxValue).
        // 
        public override void SetLength(long value)
        {
            if (value < 0 || value > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "value does not fit the length (out of range)");
            }

            // Origin wasn't publicly exposed above.
            if (value > (Int32.MaxValue - origin))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "value is too big");
            }

            int newLength = origin + (int)value;
            bool allocatedNewArray = EnsureCapacity(newLength);
            if (!allocatedNewArray && newLength > length)
                Array.Clear(internalBuffer, length, newLength - length);
            length = newLength;
            if (position > newLength) position = newLength;

        }

        public byte[] ToArray()
        {
            byte[] copy = new byte[length - origin];
            Buffer.BlockCopy(internalBuffer, origin, copy, 0, length - origin);
            return copy;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("RewritableStream does not support write method!");
        }
    }
}