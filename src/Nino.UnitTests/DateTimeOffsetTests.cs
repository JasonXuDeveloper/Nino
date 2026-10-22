using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

namespace Nino.UnitTests;

/// <summary>
/// DateTimeOffset is read and written through its fields where their positions were found (ValueViews): the bytes
/// and the values must be those of the public API, the ends of the range included, and what the constructor rejects
/// must throw as invalid data.
/// </summary>
[TestClass]
public class DateTimeOffsetTests
{
    private static readonly DateTimeOffset[] Values =
    {
        DateTimeOffset.MinValue,
        DateTimeOffset.MaxValue,
        new DateTimeOffset(1, 1, 1, 14, 0, 0, TimeSpan.FromHours(14)),
        new DateTimeOffset(9999, 12, 31, 9, 59, 59, TimeSpan.FromHours(-14)),
        new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(-150)),
        new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(345)),
        new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero),
    };

    [TestMethod]
    public void FieldsAreFound() => Assert.AreNotEqual(-1, ValueViews.DateTimeOffsetTicks);

    private static byte[] Expected(DateTimeOffset value)
    {
        var bytes = new byte[10];
        BitConverter.GetBytes(value.UtcTicks).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)value.Offset.TotalMinutes).CopyTo(bytes, 8);
        return bytes;
    }

    [TestMethod]
    public void BytesAndValuesAreThoseOfTheApi()
    {
        foreach (var value in Values)
        {
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(Expected(value), bytes);
            var back = NinoDeserializer.Deserialize<DateTimeOffset>(bytes);
            Assert.AreEqual(value.UtcTicks, back.UtcTicks);
            Assert.AreEqual(value.Offset, back.Offset);
            Assert.AreEqual(value.DateTime, back.DateTime);
        }

        // Arrays and lists take the block loops.
        var array = Enumerable.Range(0, 40).Select(i => Values[i % Values.Length]).ToArray();
        var arrayBytes = NinoSerializer.Serialize(array);
        CollectionAssert.AreEqual(array.SelectMany(Expected).ToArray(), arrayBytes.Skip(4).ToArray());
        var arrayBack = NinoDeserializer.Deserialize<DateTimeOffset[]>(arrayBytes);
        CollectionAssert.AreEqual(array.Select(v => (v.UtcTicks, v.Offset)).ToArray(),
            arrayBack.Select(v => (v.UtcTicks, v.Offset)).ToArray());
        CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<System.Collections.Generic.List<DateTimeOffset>>(
            NinoSerializer.Serialize(array.ToList())));
    }

    private static byte[] Wire(long utc, short minutes)
    {
        var bytes = new byte[10];
        BitConverter.GetBytes(utc).CopyTo(bytes, 0);
        BitConverter.GetBytes(minutes).CopyTo(bytes, 8);
        return bytes;
    }

    [TestMethod]
    public void InvalidDataThrowsAsInvalidData()
    {
        (long Utc, short Minutes)[] invalid =
        {
            (0, -60), // the local time is before the minimum
            (DateTime.MaxValue.Ticks, 60), // after the maximum
            (DateTime.MaxValue.Ticks + 1, 0),
            (-1, 0),
            (TimeSpan.TicksPerDay, 841), // more than 14 hours
            (TimeSpan.TicksPerDay, -841),
        };
        foreach (var (utc, minutes) in invalid)
        {
            long offset = minutes * TimeSpan.TicksPerMinute;
            Type expected = null;
            try
            {
                _ = new DateTimeOffset(utc + offset, new TimeSpan(offset));
            }
            catch (Exception e)
            {
                expected = e.GetType();
            }

            // What the constructor rejects is invalid data, reported as Nino's (not the constructor's) exception.
            Assert.IsNotNull(expected, $"{utc} {minutes}");
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                NinoDeserializer.Deserialize<DateTimeOffset>(Wire(utc, minutes)));
            StringAssert.StartsWith(error.Message, "Nino:");
            var arrayBytes = new byte[] { 1, 0, 0, 0 }.Concat(Wire(utc, minutes)).ToArray();
            error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                NinoDeserializer.Deserialize<DateTimeOffset[]>(arrayBytes));
            StringAssert.StartsWith(error.Message, "Nino:");
        }
    }
}
