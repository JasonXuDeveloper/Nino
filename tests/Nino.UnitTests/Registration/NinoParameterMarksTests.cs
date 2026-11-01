using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.UnitTests;

// A formatter of the user's whose parameter is [NinoRaw] (and unmanaged): the generated registry registers its marks.
[assembly: NinoFormatter(typeof(CellFormatter<>))]

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
    }
}
