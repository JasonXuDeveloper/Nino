using System;
using System.IO;
using Nino.Shared.Mgr;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public partial class Writer
    {
        /// <summary>
        /// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
        /// </summary>
        /// <param name="val"></param>
        /// <exception cref="InvalidDataException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCommonVal<T>(T val) =>
            Serializer.Serialize(typeof(T), val, this, option, false);

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        /// <param name="val"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Write<T>(ref T val, byte len) where T : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer.AsSpan(position, len).GetPinnableReference(), val);
            position += len;
        }

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void CompressAndWriteEnum<T>(T val)
		{
			var type = typeof(T);
			if (type == ConstMgr.ObjectType)
			{
				type = val.GetType();
				switch (TypeModel.GetTypeCode(type))
				{
					case TypeCode.Byte:
						buffer[position++] = Unsafe.Unbox<byte>(val);
						return;
					case TypeCode.SByte:
						buffer[position++] = *(byte*)Unsafe.Unbox<sbyte>(val);
						return;
					case TypeCode.Int16:
						Unsafe.As<byte, short>(ref buffer.AsSpan(position, 2).GetPinnableReference()) =
							Unsafe.Unbox<short>(val);
						position += 2;
						return;
					case TypeCode.UInt16:
						Unsafe.As<byte, ushort>(ref buffer.AsSpan(position, 2).GetPinnableReference()) =
							Unsafe.Unbox<ushort>(val);
						position += 2;
						return;
					case TypeCode.Int32:
						CompressAndWrite(ref Unsafe.Unbox<int>(val));
						return;
					case TypeCode.UInt32:
						CompressAndWrite(ref Unsafe.Unbox<uint>(val));
						return;
					case TypeCode.Int64:
						CompressAndWrite(ref Unsafe.Unbox<long>(val));
						return;
					case TypeCode.UInt64:
						CompressAndWrite(ref Unsafe.Unbox<ulong>(val));
						return;
				}

				return;
			}

			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
				case TypeCode.SByte:
					Unsafe.WriteUnaligned(buffer.Data + position++, val);
					return;
				case TypeCode.Int16:
				case TypeCode.UInt16:
					Unsafe.WriteUnaligned(ref buffer.AsSpan(position, 2).GetPinnableReference(), val);
					position += 2;
					return;
				case TypeCode.Int32:
					CompressAndWrite(ref Unsafe.As<T, int>(ref val));
					return;
				case TypeCode.UInt32:
					CompressAndWrite(ref Unsafe.As<T, uint>(ref val));
					return;
				case TypeCode.Int64:
					CompressAndWrite(ref Unsafe.As<T, long>(ref val));
					return;
				case TypeCode.UInt64:
					CompressAndWrite(ref Unsafe.As<T, ulong>(ref val));
					return;
			}
		}

		/// <summary>
		/// Write array
		/// </summary>
		/// <param name="arr"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(T[] arr)
		{
			//null
			if (arr == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (arr.Length == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			int len = arr.Length;
			CompressAndWrite(ref len);
			//write item
			int i = 0;
			while (i < len)
			{
				WriteCommonVal(arr[i++]);
			}
		}

		/// <summary>
		/// Write nullable
		/// </summary>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(T? val) where T: struct
		{
			//null
			if (val == null)
			{
				Write(false);
				return;
			}
			Write(true);
			WriteCommonVal(val.Value);
		}

		/// <summary>
		/// Write List
		/// </summary>
		/// <param name="lst"></param>
		/// <typeparam name="T"></typeparam>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(List<T> lst)
		{
			//null
			if (lst == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (lst.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			CompressAndWrite(lst.Count);
			//write item
			foreach (var c in lst)
			{
				WriteCommonVal(c);
			}
		}

		/// <summary>
		/// Write List
		/// </summary>
		/// <param name="lst"></param>
		/// <typeparam name="T"></typeparam>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(HashSet<T> lst)
		{
			//null
			if (lst == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (lst.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			CompressAndWrite(lst.Count);
			//write item
			foreach (var c in lst)
			{
				WriteCommonVal(c);
			}
		}

		/// <summary>
		/// Write List
		/// </summary>
		/// <param name="lst"></param>
		/// <typeparam name="T"></typeparam>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(Queue<T> lst)
		{
			//null
			if (lst == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (lst.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			CompressAndWrite(lst.Count);
			//write item
			foreach (var c in lst)
			{
				WriteCommonVal(c);
			}
		}

		/// <summary>
		/// Write List
		/// </summary>
		/// <param name="lst"></param>
		/// <typeparam name="T"></typeparam>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<T>(Stack<T> lst)
		{
			//null
			if (lst == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (lst.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			CompressAndWrite(lst.Count);
			//write item
			foreach (var c in lst)
			{
				WriteCommonVal(c);
			}
		}

		/// <summary>
		/// Write dictionary
		/// </summary>
		/// <param name="dictionary"></param>
		/// <typeparam name="TKey"></typeparam>
		/// <typeparam name="TValue"></typeparam>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<TKey, TValue>(Dictionary<TKey,TValue> dictionary)
		{
			//null
			if (dictionary == null)
			{
				Write(false);
				return;
			}
			Write(true);
			//empty
			if (dictionary.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			int len = dictionary.Count;
			CompressAndWrite(ref len);
			//record keys
			var keys = dictionary.Keys;
			//write items
			foreach (var c in keys)
			{
				//write key
				WriteCommonVal(c);
				//write val
				WriteCommonVal(dictionary[c]);
			}
		}
    }
}