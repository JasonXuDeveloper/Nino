using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.UnitTests;

// A formatter of the user's whose parameter is [NinoRaw] (and unmanaged): the generated registry registers its marks.
[assembly: NinoFormatter(typeof(CellFormatter<>))]
[assembly: NinoFormatter(typeof(WideFormatter<,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,>))]

namespace Nino.UnitTests
{
    /// <summary>One value, or null: a type Nino has no formatter for.</summary>
    public sealed class Cell<T>
    {
        public T Value;
    }

    /// <summary>A presence byte, then the value as raw bytes: only for types written raw ([NinoRaw]).</summary>
    public struct CellFormatter<[NinoRaw] T> : INinoFormatter<Cell<T>> where T : unmanaged
    {
        public void Serialize(Cell<T> value, ref Writer writer)
        {
            writer.Write(value != null);
            if (value != null)
                writer.WriteValue(value.Value);
        }

        public void Deserialize(ref Cell<T> value, ref Reader reader)
        {
            reader.Read(out bool present);
            value = present ? new Cell<T> { Value = reader.ReadValue<T>() } : null;
        }
    }

    /// <summary>A type with more type parameters than an int has bits: their marks are registered by position.</summary>
    public sealed class Wide<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
        T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, T33>
    {
        public T33 Last;
    }

    /// <summary>Writes <c>Wide</c>'s last value raw: only its last parameter is [NinoRaw] and unmanaged.</summary>
    public struct WideFormatter<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
        T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, [NinoRaw] T33>
        : INinoFormatter<Wide<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
            T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, T33>>
        where T33 : unmanaged
    {
        public void Serialize(Wide<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
            T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, T33> value, ref Writer writer) => writer.WriteValue(value.Last);

        public void Deserialize(ref Wide<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
            T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, T33> value, ref Reader reader) =>
            value = new Wide<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16,
                T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31, T32, T33> { Last = reader.ReadValue<T33>() };
    }

    /// <summary>
    /// The marks NinoParameterMarks registers for Nino.Core's formatters, used where a runtime keeps no attributes on
    /// generic parameters (IL2CPP), list every [NinoRaw], unmanaged and [NinoCreated] parameter of Nino.Core's
    /// formatters, as the attributes do here.
    /// </summary>
    [TestClass]
    public class NinoParameterMarksTests
    {
        [TestMethod]
        public void TableMatchesTheAttributes()
        {
            Assert.IsTrue(NinoParameterMarks.Visible);
            var parameters = typeof(NinoSerializer).Assembly.GetTypes()
                .Where(t => t.IsGenericTypeDefinition && t.Namespace == "Nino.Core" && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INinoFormatter<>)))
                .SelectMany(t => t.GetGenericArguments()).ToArray();
            string Name(System.Type p) => $"{p.DeclaringType!.Name}[{p.GenericParameterPosition}]";
            var marked = parameters.Select(p => $"{Name(p)} raw={NinoParameterMarks.Raw(p)} " +
                                                $"unmanaged={NinoParameterMarks.Unmanaged(p)} created={NinoParameterMarks.Created(p)}")
                .OrderBy(s => s).ToArray();
            var table = parameters.Select(p => $"{Name(p)} raw={NinoParameterMarks.Registered(p, NinoParameterMarks.Mark.Raw)} " +
                                               $"unmanaged={NinoParameterMarks.Registered(p, NinoParameterMarks.Mark.Unmanaged)} created={NinoParameterMarks.Registered(p, NinoParameterMarks.Mark.Created)}")
                .OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(marked, table, string.Join("\n", marked));
        }

        [TestMethod]
        public void GeneratedRegistryRegistersUserFormatterMarks()
        {
            // Registered by this assembly's generated registry (FormatterRules.OwnMarks), before its registrations reach
            // the resolver: what IL2CPP, which drops the attributes, binds by.
            NinoRegistry.EnsureInitialized(typeof(CellFormatter<>).Assembly);
            var parameter = typeof(CellFormatter<>).GetGenericArguments()[0];
            Assert.IsTrue(NinoParameterMarks.Registered(parameter, NinoParameterMarks.Mark.Raw));
            Assert.IsTrue(NinoParameterMarks.Registered(parameter, NinoParameterMarks.Mark.Unmanaged));
            Assert.IsFalse(NinoParameterMarks.Registered(parameter, NinoParameterMarks.Mark.Created));
            var cell = NinoDeserializer.Deserialize<Cell<long>>(NinoSerializer.Serialize(new Cell<long> { Value = -7 }));
            Assert.AreEqual(-7L, cell.Value);
        }

        [TestMethod]
        public void MarksOfParametersPastTheThirtySecondAreTheirOwn()
        {
            // A bit per parameter in an int wrapped at 32: [NinoRaw] on parameter 33 marked parameter 1 instead.
            NinoRegistry.EnsureInitialized(typeof(WideFormatter<,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,>).Assembly);
            var parameters = typeof(WideFormatter<,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,>).GetGenericArguments();
            for (int i = 0; i < parameters.Length; i++)
            {
                bool marked = i == 33;
                Assert.AreEqual(marked, NinoParameterMarks.Registered(parameters[i], NinoParameterMarks.Mark.Raw), $"T{i}");
                Assert.AreEqual(marked, NinoParameterMarks.Registered(parameters[i], NinoParameterMarks.Mark.Unmanaged), $"T{i}");
                Assert.IsFalse(NinoParameterMarks.Registered(parameters[i], NinoParameterMarks.Mark.Created), $"T{i}");
            }
        }
    }
}
